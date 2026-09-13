using System.Globalization;

namespace TanssGitConnector.Git;

/// <summary>
/// Liest aus einem Arbeitsverzeichnis, was zum Buchen eines Commits nötig ist.
/// </summary>
/// <remarks>
/// <para><b>Nur lesende Aufrufe.</b> Dieses Werkzeug verändert kein Repository — es legt keinen
/// Commit an, es ändert keine Marke, es schreibt in keine Git-Konfiguration außer beim
/// ausdrücklichen Einrichten des Hooks. Wer hier einen schreibenden Aufruf ergänzt, ändert
/// diese Zusage.</para>
/// <para><b>Die Zerlegung hängt an einem Trennzeichen, das in keinem Text vorkommt:</b>
/// <c>%x1f</c>, die Unit Separator. Ein Zeilenumbruch als Trenner wäre falsch — Commit-Meldungen
/// bestehen aus Zeilenumbrüchen —, und ein Tabulator kommt in Betreffzeilen durchaus vor.</para>
/// </remarks>
public sealed class GitRepository
{
    /// <summary>Das Trennzeichen zwischen den Feldern der <c>git log</c>-Ausgabe.</summary>
    private const char Separator = '\u001f';

    /// <summary>
    /// Das Ausgabeformat. Der Rumpf steht am Ende, weil er als einziges Feld Zeilenumbrüche
    /// enthält und deshalb keinen Nachfolger haben darf, der von ihm abgegrenzt werden müsste.
    /// </summary>
    private const string LogFormat = "%H%x1f%P%x1f%ct%x1f%an%x1f%ae%x1f%s%x1f%B";

    private readonly IGitRunner _git;

    /// <summary>Baut den Zugriff.</summary>
    /// <param name="git">Der Aufrufer für <c>git</c>.</param>
    public GitRepository(IGitRunner git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
    }

    /// <summary>
    /// Liest einen Commit samt Zweig und Repositoryname.
    /// </summary>
    /// <param name="directory">Ein Verzeichnis innerhalb des Repositorys.</param>
    /// <param name="revision">Die Version; ohne Angabe <c>HEAD</c>.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <exception cref="NotARepositoryException">
    /// Das Verzeichnis gehört zu keinem Repository, oder das Repository hat noch keinen Commit.
    /// </exception>
    public async Task<CommitInfo> ReadCommitAsync(string directory, string revision = "HEAD",
                                                  CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);

        string top = await TopLevelAsync(directory, ct).ConfigureAwait(false);

        GitResult log = await _git.RunAsync(directory,
            ["log", "--max-count=1", "--format=" + LogFormat, revision, "--"], ct)
            .ConfigureAwait(false);

        if (!log.Succeeded)
        {
            throw new NotARepositoryException(
                $"„{revision}“ liess sich in {top} nicht lesen. Bei einem frisch angelegten "
                + "Repository ohne Commit ist das der Normalfall; sonst nennt Git den Grund: "
                + log.StandardError, log.ExitCode);
        }

        CommitInfo commit = Parse(log.StandardOutput);

        return commit with
        {
            Branch = await CurrentBranchAsync(directory, ct).ConfigureAwait(false),
            RepositoryPath = top,
            RepositoryName = NameOf(top),
        };
    }

    /// <summary>
    /// Zerlegt eine Zeile im Format <see cref="LogFormat"/>.
    /// </summary>
    /// <remarks>
    /// Öffentlich, weil genau hier die Fälle sitzen, die man im Alltag selten erzeugt: ein
    /// leerer Betreff, ein mehrzeiliger Rumpf, ein Wurzelcommit ohne Eltern. Sie gehören
    /// geprüft, und zwar ohne dass dafür ein echtes Repository angelegt werden muss.
    /// </remarks>
    /// <param name="payload">Die Ausgabe von <c>git log</c> für genau einen Commit.</param>
    /// <exception cref="GitException">Die Ausgabe hat nicht die erwartete Form.</exception>
    public static CommitInfo Parse(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        string[] fields = payload.Split(Separator);
        if (fields.Length < 7)
        {
            throw new GitException(
                $"Die Ausgabe von git log hat {fields.Length} statt sieben Feldern. Das passt zu "
                + "einer geänderten Git-Version oder zu einer Ausgabe, die nicht von diesem "
                + "Werkzeug angefordert wurde. Gebucht wird nichts, solange die Herkunft der "
                + "Zeiten und Texte nicht feststeht.");
        }

        if (!long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture,
                           out long seconds))
        {
            throw new GitException(
                $"Git hat als Commit-Zeitpunkt „{fields[2]}“ geliefert, und das sind keine "
                + "Unix-Sekunden. Ein geratener Zeitpunkt wäre gebuchte Arbeitszeit, die nie "
                + "stattgefunden hat.");
        }

        return new CommitInfo
        {
            Sha = fields[0].Trim(),
            Parents = fields[1].Trim(),
            CommittedAt = DateTimeOffset.FromUnixTimeSeconds(seconds),
            AuthorName = fields[3],
            AuthorEmail = fields[4],
            Subject = fields[5],

            // Die restlichen Felder wieder zusammensetzen: Enthielte ein Rumpf jemals selbst
            // das Trennzeichen, ginge sonst alles dahinter verloren.
            Message = string.Join(Separator, fields[6..]).TrimEnd('\r', '\n'),
        };
    }

    /// <summary>Das Wurzelverzeichnis des Repositorys, zu dem dieses Verzeichnis gehört.</summary>
    /// <exception cref="NotARepositoryException">Das Verzeichnis gehört zu keinem Repository.</exception>
    public async Task<string> TopLevelAsync(string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        GitResult result = await _git
            .RunAsync(directory, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false);

        if (!result.Succeeded || result.StandardOutput.Length == 0)
        {
            throw new NotARepositoryException(
                $"{directory} gehört zu keinem Git-Repository. " + result.StandardError,
                result.ExitCode);
        }

        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Das Verzeichnis, in dem die Hooks dieses Repositorys liegen.
    /// </summary>
    /// <remarks>
    /// Über <c>git rev-parse --git-path hooks</c> und nicht über <c>.git/hooks</c>
    /// zusammengesetzt. Der Unterschied ist kein Feinschliff: Bei einem Arbeitsbaum
    /// (<c>git worktree</c>) ist <c>.git</c> eine Datei, und wer <c>core.hooksPath</c> gesetzt
    /// hat, liegt ganz woanders. Ein Hook im falschen Verzeichnis läuft nie und sagt auch nicht,
    /// warum.
    /// </remarks>
    public async Task<string> HooksDirectoryAsync(string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        GitResult result = await _git
            .RunAsync(directory, ["rev-parse", "--git-path", "hooks"], ct).ConfigureAwait(false);

        if (!result.Succeeded || result.StandardOutput.Length == 0)
        {
            throw new NotARepositoryException(
                $"{directory} gehört zu keinem Git-Repository. " + result.StandardError,
                result.ExitCode);
        }

        string path = result.StandardOutput.Trim();

        // Git antwortet relativ zum Arbeitsverzeichnis. Ein relativer Pfad, der spaeter in
        // einer Meldung steht oder in eine andere Schicht wandert, zeigt dort auf etwas
        // anderes.
        return Path.GetFullPath(path, directory);
    }

    /// <summary>
    /// Der Zweig, auf dem HEAD steht — oder eine leere Zeichenkette bei abgetrenntem HEAD.
    /// </summary>
    /// <remarks>
    /// <c>symbolic-ref</c> und nicht <c>rev-parse --abbrev-ref HEAD</c>: Letzteres liefert bei
    /// abgetrenntem HEAD das Wort <c>HEAD</c>, und aus einem Zweig namens „HEAD“ würde die
    /// Ticketsuche dann munter eine Nummer zu lesen versuchen. Ein leerer Wert sagt klar, dass
    /// es keinen Zweig gibt.
    /// </remarks>
    public async Task<string> CurrentBranchAsync(string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        GitResult result = await _git
            .RunAsync(directory, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct)
            .ConfigureAwait(false);

        return result.Succeeded ? result.StandardOutput.Trim() : string.Empty;
    }

    /// <summary>
    /// Wann der Commit davor entstanden ist — oder <see langword="null"/> beim Wurzelcommit.
    /// </summary>
    /// <remarks>
    /// <para>Grundlage der Dauer im Modus <c>seit_letztem_commit</c>. Gefragt wird entlang des
    /// <b>ersten</b> Elternteils: Nach einer Zusammenführung wäre der nächstjüngere Commit sonst
    /// einer aus dem zusammengeführten Zweig, und die Dauer bemässe sich an fremder Arbeit.</para>
    /// <para>Der Wurzelcommit hat keinen Vorgänger. Das ist kein Fehler, sondern die Antwort
    /// „lässt sich nicht bemessen“ — der Aufrufer nimmt dann die Vorgabedauer.</para>
    /// </remarks>
    public async Task<DateTimeOffset?> PreviousCommitTimeAsync(string directory, string revision,
                                                               CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);

        GitResult result = await _git.RunAsync(directory,
            ["log", "--first-parent", "--max-count=2", "--format=%ct", revision, "--"], ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return null;
        }

        string[] lines = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines.Length >= 2
               && long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                                out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>Der Name des Repositorys aus seinem Wurzelverzeichnis.</summary>
    /// <remarks>
    /// Git gibt den Pfad mit Schrägstrichen aus, auch unter Windows. Deshalb wird an beiden
    /// Trennzeichen geschnitten statt über <see cref="Path.GetFileName(string)"/>, das unter
    /// Windows am umgekehrten Schrägstrich schneidet und den ganzen Pfad zurückgäbe.
    /// </remarks>
    public static string NameOf(string topLevel)
    {
        ArgumentNullException.ThrowIfNull(topLevel);

        string trimmed = topLevel.TrimEnd('/', '\\');
        int cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }
}
