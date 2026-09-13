using System.Globalization;
using System.Text;

namespace TanssGitConnector.Git;

/// <summary>Was an der Stelle des Hooks vorgefunden wurde.</summary>
public enum HookState
{
    /// <summary>Es liegt kein <c>post-commit</c>-Hook.</summary>
    Missing,

    /// <summary>Unser Hook liegt dort.</summary>
    Ours,

    /// <summary>Dort liegt ein fremder Hook. Er wird nicht angefasst.</summary>
    Foreign,

    /// <summary>Die Datei liegt dort, liess sich aber nicht lesen.</summary>
    /// <remarks>
    /// Ausdrücklich nicht <see cref="Missing"/>: Eine unlesbare Datei als „nicht vorhanden“ zu
    /// melden, führte dazu, dass darüber geschrieben wird, ohne zu wissen, was verloren geht.
    /// </remarks>
    Unreadable,
}

/// <summary>Der Befund zu einem Hook-Verzeichnis.</summary>
/// <param name="State">Was vorgefunden wurde.</param>
/// <param name="Path">Der vollständige Pfad der Hook-Datei.</param>
/// <param name="Detail">Ein erklärender Satz, oder <see langword="null"/>.</param>
public sealed record HookStatus(HookState State, string Path, string? Detail = null)
{
    /// <summary>Ist der Hook eingerichtet?</summary>
    public bool IsInstalled => State == HookState.Ours;
}

/// <summary>Das Einrichten des Hooks ist nicht möglich, ohne etwas Fremdes zu zerstören.</summary>
public sealed class HookException : GitException
{
    public HookException(string message, Exception? inner = null) : base(message, inner: inner) { }
}

/// <summary>
/// Richtet den <c>post-commit</c>-Hook ein und wieder ab — im einzelnen Repository und als
/// Vorlage für künftige.
/// </summary>
/// <remarks>
/// <para><b>Fremde Hook bleiben unangetastet.</b> Eine vorgefundene Datei ohne unsere
/// Erkennungsmarke wird weder überschrieben noch gelöscht, sondern gemeldet. Ein
/// <c>post-commit</c>-Hook kann ein Prüflauf, eine Signatur oder eine Benachrichtigung sein.
/// Erst <c>--force</c> überschreibt, und auch dann wird der bisherige Stand daneben
/// aufbewahrt.</para>
///
/// <para><b>Die Vorlage wirkt nur auf neue Repositorys.</b> <c>init.templatedir</c> greift bei
/// <c>git init</c> und <c>git clone</c>. Bestehende Repositorys bekommen den Hook über
/// <c>tanss-git enable</c> im jeweiligen Verzeichnis — oder über ein erneutes <c>git init</c>,
/// das an einem vorhandenen Repository nichts ändert, aber die Vorlage nachträgt.</para>
/// </remarks>
public sealed class HookInstaller
{
    /// <summary>Der Unterordner der Vorlage, in dem Git seine Hooks sucht.</summary>
    public const string TemplateHooksFolder = "hooks";

    /// <summary>Der Git-Einstellungsname der Vorlage.</summary>
    public const string TemplateSetting = "init.templatedir";

    private readonly IGitRunner _git;
    private readonly GitRepository _repository;

    /// <summary>Baut den Einrichter.</summary>
    /// <param name="git">Der Aufrufer für <c>git</c>.</param>
    public HookInstaller(IGitRunner git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
        _repository = new GitRepository(git);
    }

    // --- Einzelnes Repository ------------------------------------------------------------

    /// <summary>Sieht nach, was im Hook-Verzeichnis dieses Repositorys liegt.</summary>
    public async Task<HookStatus> InspectRepositoryAsync(string directory,
                                                         CancellationToken ct = default)
    {
        string hooks = await _repository.HooksDirectoryAsync(directory, ct).ConfigureAwait(false);
        return Inspect(hooks);
    }

    /// <summary>Richtet den Hook in diesem Repository ein.</summary>
    /// <param name="directory">Ein Verzeichnis innerhalb des Repositorys.</param>
    /// <param name="executablePath">Der vollständige Pfad zu <c>tanss-git</c>.</param>
    /// <param name="force">Darf ein fremder Hook ersetzt werden? Der bisherige wird gesichert.</param>
    /// <param name="ct">Abbruchmarke.</param>
    public async Task<HookStatus> EnableRepositoryAsync(string directory, string executablePath,
                                                        bool force = false,
                                                        CancellationToken ct = default)
    {
        string hooks = await _repository.HooksDirectoryAsync(directory, ct).ConfigureAwait(false);
        return Install(hooks, executablePath, force);
    }

    /// <summary>Entfernt den Hook aus diesem Repository — sofern es unserer ist.</summary>
    public async Task<HookStatus> DisableRepositoryAsync(string directory,
                                                         CancellationToken ct = default)
    {
        string hooks = await _repository.HooksDirectoryAsync(directory, ct).ConfigureAwait(false);
        return Remove(hooks);
    }

    // --- Vorlage für künftige Repositorys ------------------------------------------------

    /// <summary>
    /// Liest <c>init.templatedir</c> aus der Git-Konfiguration des Benutzers.
    /// </summary>
    /// <returns>Der eingetragene Pfad, oder <see langword="null"/>, wenn keiner gesetzt ist.</returns>
    public async Task<string?> ConfiguredTemplateDirectoryAsync(CancellationToken ct = default)
    {
        GitResult result = await _git
            .RunAsync(Environment.CurrentDirectory, ["config", "--global", "--get", TemplateSetting], ct)
            .ConfigureAwait(false);

        // Rueckgabewert 1 heisst bei "config --get" schlicht "nicht gesetzt" und ist kein
        // Fehler. Alles andere waere einer - dann bleibt es bei "nicht gesetzt", und der
        // Aufrufer erfaehrt beim Schreiben, woran es liegt.
        return result.Succeeded && result.StandardOutput.Trim() is { Length: > 0 } value
            ? value
            : null;
    }

    /// <summary>
    /// Trägt eine Vorlage in die Git-Konfiguration des Benutzers ein.
    /// </summary>
    /// <remarks>
    /// <b>Geschrieben wird in <c>--global</c>, also in die Konfiguration des Benutzers</b> und
    /// nicht in die des Rechners. Ein Werkzeug, das die Arbeitszeit genau eines Technikers
    /// bucht, hat in einer rechnerweiten Einstellung nichts zu suchen — auf einem
    /// Terminalserver träfe es sonst alle.
    /// </remarks>
    public async Task SetTemplateDirectoryAsync(string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // Git schreibt Pfade in seiner Konfiguration mit gewoehnlichen Schraegstrichen; unter
        // Windows liest es umgekehrte zwar auch, macht daraus aber beim naechsten Schreiben
        // eine Mischform, die niemand mehr vergleichen kann.
        string value = directory.Replace('\\', '/');

        GitResult result = await _git
            .RunAsync(Environment.CurrentDirectory, ["config", "--global", TemplateSetting, value], ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new HookException(
                $"„{TemplateSetting}“ liess sich nicht setzen: {result.StandardError}. Ohne diese "
                + "Einstellung bekommen neu angelegte Repositorys den Hook nicht; bestehende "
                + "lassen sich weiterhin einzeln einrichten.");
        }
    }

    /// <summary>
    /// Entfernt <c>init.templatedir</c> — aber nur, wenn dort unsere Vorlage steht.
    /// </summary>
    /// <remarks>
    /// Zeigt die Einstellung woandershin, bleibt sie stehen. Wer eine eigene Vorlage pflegt,
    /// verliert sie nicht dadurch, dass er dieses Werkzeug abschaltet; entfernt wird dann nur
    /// unser Hook aus seiner Vorlage.
    /// </remarks>
    /// <param name="ownTemplateDirectory">Der Pfad unserer Vorlage.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns><see langword="true"/>, wenn die Einstellung entfernt wurde.</returns>
    public async Task<bool> UnsetTemplateDirectoryAsync(string ownTemplateDirectory,
                                                        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownTemplateDirectory);

        string? configured = await ConfiguredTemplateDirectoryAsync(ct).ConfigureAwait(false);
        if (configured is null || !SamePath(configured, ownTemplateDirectory))
        {
            return false;
        }

        GitResult result = await _git
            .RunAsync(Environment.CurrentDirectory, ["config", "--global", "--unset", TemplateSetting], ct)
            .ConfigureAwait(false);

        return result.Succeeded;
    }

    /// <summary>Zeigen beide Angaben auf dasselbe Verzeichnis?</summary>
    /// <remarks>
    /// Verglichen wird über <see cref="Path.GetFullPath(string)"/>, nicht Zeichen für Zeichen:
    /// Git schreibt gewöhnliche Schrägstriche, Windows liefert umgekehrte, und ein abschliessender
    /// Schrägstrich kommt je nach Eingabe dazu oder nicht.
    /// </remarks>
    public static bool SamePath(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        try
        {
            string one = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            string other = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));

            return string.Equals(one, other, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            // Ein Pfad, der sich nicht aufloesen laesst, ist nicht derselbe wie irgendeiner.
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    // --- Die Dateiarbeit -----------------------------------------------------------------

    /// <summary>Sieht nach, was in einem Hook-Verzeichnis liegt.</summary>
    public static HookStatus Inspect(string hooksDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hooksDirectory);

        string path = Path.Combine(hooksDirectory, HookScript.FileName);

        if (!File.Exists(path))
        {
            return new HookStatus(HookState.Missing, path);
        }

        try
        {
            string content = File.ReadAllText(path, Encoding.UTF8);

            return HookScript.IsOurs(content)
                ? new HookStatus(HookState.Ours, path)
                : new HookStatus(HookState.Foreign, path,
                    "Dort liegt bereits ein anderer post-commit-Hook. Er wird nicht angefasst.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HookStatus(HookState.Unreadable, path, ex.Message);
        }
    }

    /// <summary>
    /// Schreibt den Hook in ein Hook-Verzeichnis.
    /// </summary>
    /// <param name="hooksDirectory">Das Verzeichnis; wird angelegt, wenn es fehlt.</param>
    /// <param name="executablePath">Der vollständige Pfad zu <c>tanss-git</c>.</param>
    /// <param name="force">Darf ein fremder Hook ersetzt werden?</param>
    /// <exception cref="HookException">
    /// Dort liegt ein fremder oder unlesbarer Hook und <paramref name="force"/> ist nicht gesetzt.
    /// </exception>
    public static HookStatus Install(string hooksDirectory, string executablePath, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hooksDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        HookStatus found = Inspect(hooksDirectory);

        if (found.State is HookState.Foreign or HookState.Unreadable && !force)
        {
            throw new HookException(
                $"In {found.Path} liegt bereits ein post-commit-Hook, der nicht von diesem "
                + "Werkzeug stammt. Er bleibt unangetastet — er kann ein Prüflauf oder eine "
                + "Benachrichtigung sein, und beides lautlos zu entfernen wäre schlimmer als "
                + "eine nicht gebuchte Arbeitszeit. Entweder den Aufruf "
                + "„tanss-git hook --repository \"$PWD\"“ von Hand in den vorhandenen Hook "
                + "aufnehmen oder mit „--force“ ersetzen; dabei wird der bisherige Stand daneben "
                + "aufbewahrt.");
        }

        Directory.CreateDirectory(hooksDirectory);

        if (found.State is HookState.Foreign or HookState.Unreadable)
        {
            Preserve(found.Path);
        }

        WriteExecutable(found.Path, HookScript.Build(executablePath));

        return new HookStatus(HookState.Ours, found.Path);
    }

    /// <summary>
    /// Entfernt unseren Hook. Ein fremder bleibt liegen.
    /// </summary>
    /// <returns>Der Befund <b>nach</b> dem Versuch.</returns>
    public static HookStatus Remove(string hooksDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hooksDirectory);

        HookStatus found = Inspect(hooksDirectory);
        if (found.State != HookState.Ours)
        {
            return found;
        }

        File.Delete(found.Path);
        return new HookStatus(HookState.Missing, found.Path);
    }

    /// <summary>
    /// Legt den bisherigen Stand einer Datei daneben ab.
    /// </summary>
    /// <remarks>
    /// Mit Zeitstempel im Namen, damit ein zweites <c>--force</c> die erste Sicherung nicht
    /// überschreibt. Eine überschriebene Sicherung ist keine.
    /// </remarks>
    private static void Preserve(string path)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        File.Copy(path, path + ".vorher-" + stamp, overwrite: false);
    }

    /// <summary>
    /// Schreibt eine Skriptdatei: UTF-8 ohne Vorspann, Unix-Zeilenenden, ausführbar.
    /// </summary>
    /// <remarks>
    /// <para><b>Ohne Byte-Order-Mark.</b> Ein Vorspann vor <c>#!/bin/sh</c> macht aus der
    /// Kennzeichnung des Interpreters Zeichensalat; die Shell führt die Datei dann gar nicht
    /// oder mit dem falschen Interpreter aus.</para>
    /// <para><b>Erst danebenschreiben, dann umbenennen.</b> Ein Abbruch mitten im Schreiben
    /// hinterliesse sonst einen halben Hook — und der läuft bei jedem Commit.</para>
    /// <para><b>Ausführbar nur, wo es das Dateisystem kennt.</b> Unter Windows gibt es kein
    /// Ausführungsrecht an der Datei; Git ruft den Hook dort über seine Bash auf.</para>
    /// </remarks>
    private static void WriteExecutable(string path, string content)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new HookException($"{path} hat kein Verzeichnis, in das geschrieben werden könnte.");

        string temporary = Path.Combine(directory,
            ".tanss-git-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        try
        {
            File.WriteAllBytes(temporary, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetBytes(content));

            if (!OperatingSystem.IsWindows())
            {
                // 0755: Der Benutzer darf schreiben, alle duerfen lesen und ausfuehren. Ohne das
                // Ausfuehrungsrecht uebergeht Git den Hook - lautlos, ohne Fehlermeldung.
                File.SetUnixFileMode(temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Eine liegengebliebene Zwischendatei ist unschoen und harmlos. Den eigentlichen
            // Fehler verdecken darf sie nicht.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
