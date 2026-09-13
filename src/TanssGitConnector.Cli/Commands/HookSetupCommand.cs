using TanssGitConnector.Git;
using TanssGitConnector.Storage;

namespace TanssGitConnector.Cli.Commands;

/// <summary>
/// Richtet den <c>post-commit</c>-Haken ein und wieder ab: <c>enable</c> und <c>disable</c>.
/// </summary>
/// <remarks>
/// <para><b>Zwei Wege, und sie schliessen sich nicht aus.</b> Ohne <c>--global</c> wird der
/// Haken in <b>dieses</b> Repository geschrieben und wirkt sofort. Mit <c>--global</c> entsteht
/// eine Vorlage, die Git bei jedem künftigen <c>git init</c> und <c>git clone</c> in das neue
/// Repository kopiert — bestehende Repositorys erreicht sie nicht.</para>
///
/// <para><b>Eine fremde Vorlage wird nicht verdrängt.</b> Steht in <c>init.templatedir</c>
/// bereits ein Verzeichnis, das nicht unseres ist, legen wir unseren Haken <i>dort</i> hinein
/// und lassen die Einstellung unberührt. Wer eine eigene Vorlage pflegt, hat Gründe dafür, und
/// sie umzubiegen nähme ihm seine anderen Haken.</para>
/// </remarks>
internal static class HookSetupCommand
{
    /// <summary>Richtet den Haken ein.</summary>
    /// <param name="composition">Die Bausteine dieses Laufs.</param>
    /// <param name="directory">Das Repository — nur ohne <paramref name="global"/> von Belang.</param>
    /// <param name="global">Als Vorlage für künftige Repositorys statt in dieses.</param>
    /// <param name="force">Einen fremden Haken ersetzen; der bisherige wird gesichert.</param>
    /// <param name="output">Die gewöhnliche Ausgabe.</param>
    /// <param name="error">Die Fehlerausgabe.</param>
    /// <param name="ct">Abbruchmarke.</param>
    public static async Task<int> EnableAsync(Composition composition, string directory,
                                              bool global, bool force, TextWriter output,
                                              TextWriter error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string executable = ExecutablePath();

        try
        {
            return global
                ? await EnableGlobalAsync(composition, executable, force, output, ct).ConfigureAwait(false)
                : await EnableHereAsync(composition, directory, executable, force, output, ct)
                    .ConfigureAwait(false);
        }
        catch (HookException exception)
        {
            error.WriteLine(exception.Message);
            return ExitCode.Broken;
        }
        catch (NotARepositoryException exception)
        {
            error.WriteLine(exception.Message);
            error.WriteLine();
            error.WriteLine("Für alle künftigen Repositorys gilt stattdessen: "
                + "tanss-git enable --global");
            return ExitCode.Broken;
        }
    }

    /// <summary>Entfernt den Haken wieder.</summary>
    public static async Task<int> DisableAsync(Composition composition, string directory,
                                               bool global, TextWriter output, TextWriter error,
                                               CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            return global
                ? await DisableGlobalAsync(composition, output, ct).ConfigureAwait(false)
                : await DisableHereAsync(composition, directory, output, ct).ConfigureAwait(false);
        }
        catch (NotARepositoryException exception)
        {
            error.WriteLine(exception.Message);
            return ExitCode.Broken;
        }
    }

    private static async Task<int> EnableHereAsync(Composition composition, string directory,
                                                   string executable, bool force,
                                                   TextWriter output, CancellationToken ct)
    {
        HookStatus status = await composition.Hooks
            .EnableRepositoryAsync(directory, executable, force, ct).ConfigureAwait(false);

        output.WriteLine("Der Haken ist eingerichtet:");
        output.WriteLine("  " + status.Path);
        output.WriteLine();
        output.WriteLine("Ab dem nächsten Commit in diesem Repository wird gebucht.");
        output.WriteLine("Probelauf ohne Buchung: tanss-git book HEAD --dry-run");

        composition.Log.Info("hook.enabled", "Haken eingerichtet: " + status.Path);
        return ExitCode.Healthy;
    }

    private static async Task<int> EnableGlobalAsync(Composition composition, string executable,
                                                     bool force, TextWriter output,
                                                     CancellationToken ct)
    {
        string? configured = await composition.Hooks.ConfiguredTemplateDirectoryAsync(ct)
            .ConfigureAwait(false);

        bool foreign = configured is { Length: > 0 }
            && !HookInstaller.SamePath(configured, StoragePaths.TemplateDirectory);

        string template = foreign ? configured! : StoragePaths.TemplateDirectory;
        string hooks = Path.Combine(template, HookInstaller.TemplateHooksFolder);

        HookStatus status = HookInstaller.Install(hooks, executable, force);

        if (!foreign)
        {
            await composition.Hooks.SetTemplateDirectoryAsync(template, ct).ConfigureAwait(false);
        }

        output.WriteLine("Die Vorlage ist eingerichtet:");
        output.WriteLine("  " + status.Path);
        output.WriteLine();

        if (foreign)
        {
            output.WriteLine("Hinweis: In init.templatedir stand bereits eine eigene Vorlage. Der");
            output.WriteLine("Haken wurde dort abgelegt, die Einstellung selbst blieb unverändert.");
            output.WriteLine();
        }

        output.WriteLine("Git kopiert diese Vorlage bei „git init“ und „git clone“ in das neue");
        output.WriteLine("Repository. **Bestehende Repositorys erreicht sie nicht** — dort gilt:");
        output.WriteLine();
        output.WriteLine("  tanss-git enable          (im jeweiligen Verzeichnis)");
        output.WriteLine("  git init                  (ändert nichts am Projekt, trägt nur die Vorlage nach)");

        composition.Log.Info("hook.enabled-global", "Vorlage eingerichtet: " + status.Path);
        return ExitCode.Healthy;
    }

    private static async Task<int> DisableHereAsync(Composition composition, string directory,
                                                    TextWriter output, CancellationToken ct)
    {
        HookStatus before = await composition.Hooks.InspectRepositoryAsync(directory, ct)
            .ConfigureAwait(false);

        switch (before.State)
        {
            case HookState.Ours:
                _ = await composition.Hooks.DisableRepositoryAsync(directory, ct).ConfigureAwait(false);
                output.WriteLine("Der Haken ist entfernt: " + before.Path);
                output.WriteLine("Commits in diesem Repository werden nicht mehr gebucht.");
                composition.Log.Info("hook.disabled", "Haken entfernt: " + before.Path);
                return ExitCode.Healthy;

            case HookState.Missing:
                output.WriteLine("Hier liegt kein Haken. Es war nichts zu tun.");
                return ExitCode.Healthy;

            default:
                // Ein fremder Haken bleibt liegen. Ihn zu entfernen, weil er zufaellig so
                // heisst wie unserer, waere ein Eingriff in die Arbeit eines anderen.
                output.WriteLine("In " + before.Path + " liegt ein post-commit-Haken, der nicht");
                output.WriteLine("von diesem Werkzeug stammt. Er bleibt unangetastet.");
                return ExitCode.Warning;
        }
    }

    private static async Task<int> DisableGlobalAsync(Composition composition, TextWriter output,
                                                      CancellationToken ct)
    {
        string? configured = await composition.Hooks.ConfiguredTemplateDirectoryAsync(ct)
            .ConfigureAwait(false);

        string template = configured is { Length: > 0 } ? configured : StoragePaths.TemplateDirectory;
        string hooks = Path.Combine(template, HookInstaller.TemplateHooksFolder);

        HookStatus after = HookInstaller.Remove(hooks);

        if (after.State == HookState.Foreign)
        {
            output.WriteLine("In der Vorlage liegt ein fremder post-commit-Haken. Er bleibt liegen.");
            return ExitCode.Warning;
        }

        bool unset = await composition.Hooks
            .UnsetTemplateDirectoryAsync(StoragePaths.TemplateDirectory, ct).ConfigureAwait(false);

        output.WriteLine("Der Haken ist aus der Vorlage entfernt.");

        if (unset)
        {
            output.WriteLine("init.templatedir ist zurückgesetzt.");
        }
        else if (configured is { Length: > 0 })
        {
            output.WriteLine("init.templatedir zeigt auf eine fremde Vorlage und bleibt stehen.");
        }

        output.WriteLine();
        output.WriteLine("Bereits eingerichtete Repositorys behalten ihren Haken. Dort gilt:");
        output.WriteLine("  tanss-git disable         (im jeweiligen Verzeichnis)");

        composition.Log.Info("hook.disabled-global", "Vorlage entfernt.");
        return ExitCode.Healthy;
    }

    /// <summary>
    /// Der vollständige Pfad zu diesem Programm.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.ProcessPath"/> und nicht der Name: Der Haken läuft später in einer
    /// Shell, deren Suchpfad niemand kennt — aus einer Entwicklungsumgebung heraus ist er
    /// regelmässig knapper als der eines Terminals. Ein Haken, der sein Programm nicht findet,
    /// scheitert lautlos bei jedem Commit.
    /// </remarks>
    internal static string ExecutablePath() =>
        Environment.ProcessPath
        ?? throw new HookException(
            "Der eigene Programmpfad liess sich nicht ermitteln. Ohne ihn kann kein Haken "
            + "geschrieben werden, der das Programm später wiederfindet.");
}
