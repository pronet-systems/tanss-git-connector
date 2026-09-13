using System.Text;
using TanssGitConnector.Api.Diagnostics;
using TanssGitConnector.Cli.CommandLine;
using TanssGitConnector.Cli.Commands;
using TanssGitConnector.Cli.Output;
using TanssGitConnector.Storage;
using TanssGitConnector.Storage.Config;

namespace TanssGitConnector.Cli;

/// <summary>
/// Der Einstiegspunkt: Befehlszeile auswerten, Bausteine bauen, Befehl ausführen.
/// </summary>
/// <remarks>
/// <para><b>Hier stürzt nichts ab.</b> Jeder Weg endet mit einem Rückgabewert und einem Satz,
/// der sagt, was los ist — auch und gerade der erste Aufruf auf einem frischen Rechner, auf dem
/// noch keine Konfiguration liegt. Ein Haken, der mit einer Ausnahme abbricht, sieht aus wie ein
/// kaputtes Werkzeug und nicht wie eine fehlende Einrichtung.</para>
///
/// <para><b>Zwei Befehle laufen ohne Konfiguration:</b> <c>setup</c> legt sie an, und
/// <c>disable</c> nimmt den Haken zurück — gerade dann, wenn die Einrichtung kaputt ist, muss
/// man ihn loswerden können, ohne sie erst zu reparieren.</para>
/// </remarks>
internal static class Program
{
    /// <summary>Startet das Werkzeug.</summary>
    /// <param name="args">Die Befehlszeile ohne den Programmnamen.</param>
    private static async Task<int> Main(string[] args)
    {
        UseUtf8Console();

        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // Cancel = true: Das Werkzeug beendet sich selbst und geordnet. Ohne diese Zeile
            // risse das Betriebssystem den Prozess ab - moeglicherweise mitten im Schreiben der
            // Warteschlange.
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        return await RunAsync(args, ConfigStore.Default(), Console.In, Console.Out, Console.Error,
                              shutdown.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Der eigentliche Ablauf — mit hereingereichtem Konfigurationsort und hereingereichten
    /// Ausgaben.
    /// </summary>
    /// <remarks>
    /// Getrennt von <c>Main</c>, damit sich prüfen lässt, was ohne Konfiguration geschieht:
    /// Diese Zusage — kein Befehl stürzt ab, jeder nennt die Einrichtung — ist nur etwas wert,
    /// wenn sie geprüft wird, und prüfen liesse sie sich sonst nur, indem ein Test das
    /// Benutzerprofil des Ausführenden leerräumt.
    /// </remarks>
    internal static async Task<int> RunAsync(string[] args, ConfigStore store, TextReader input,
                                             TextWriter output, TextWriter error,
                                             CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        CliArgs parsed = CliArgs.Parse(args);

        if (parsed.HelpRequested)
        {
            HelpText.Write(output);
            return ExitCode.Healthy;
        }

        if (parsed.VersionRequested)
        {
            output.WriteLine("tanss-git " + HelpText.Version);
            return ExitCode.Healthy;
        }

        if (parsed.Error is { Length: > 0 } problem)
        {
            foreach (string line in Report.Wrap(problem, 92))
            {
                error.WriteLine(line);
            }

            return ExitCode.Usage;
        }

        try
        {
            return await DispatchAsync(parsed, store, input, output, error, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Abgebrochen.");

            // Ein Abbruch durch den Benutzer ist kein Fehler - aber auch kein erledigter
            // Vorgang. Der Haken macht daraus ohnehin eine 0.
            return ExitCode.Warning;
        }
        catch (Exception exception)
        {
            // Der letzte Riegel. Was hier ankommt, ist ein Programmfehler - er soll trotzdem als
            // Satz erscheinen und nicht als Stapelabzug, und mit einem Rueckgabewert, den eine
            // Ueberwachung lesen kann.
            error.WriteLine("Unerwarteter Fehler. Das ist ein Programmfehler und keine Frage der "
                + "Einrichtung; die folgende Meldung gehört in die Fehlermeldung an den "
                + "Hersteller.");
            error.WriteLine(Redaction.Scrub(exception.Message));
            return ExitCode.Broken;
        }
    }

    /// <summary>Führt den erkannten Befehl aus.</summary>
    private static async Task<int> DispatchAsync(CliArgs parsed, ConfigStore store,
                                                 TextReader input, TextWriter output,
                                                 TextWriter error, CancellationToken ct)
    {
        string directory = parsed.Repository ?? Environment.CurrentDirectory;

        if (parsed.Command == CliCommand.Setup)
        {
            return await SetupCommand.RunAsync(store, input, output, error, ct).ConfigureAwait(false);
        }

        AppConfig? config = TryLoad(store, out string? reason);

        if (config is null)
        {
            return await WithoutConfigurationAsync(parsed, store, directory, reason, output, error, ct)
                .ConfigureAwait(false);
        }

        using Composition composition = new(config);

        return parsed.Command switch
        {
            CliCommand.Doctor =>
                await DoctorCommand.RunAsync(composition, store.Path, directory, output, ct)
                    .ConfigureAwait(false),

            CliCommand.Status =>
                await StatusCommand.RunAsync(composition, store.Path, directory, output, ct)
                    .ConfigureAwait(false),

            CliCommand.Enable =>
                await HookSetupCommand.EnableAsync(composition, directory, parsed.Global,
                                                   parsed.Force, output, error, ct)
                    .ConfigureAwait(false),

            CliCommand.Disable =>
                await HookSetupCommand.DisableAsync(composition, directory, parsed.Global, output,
                                                    error, ct).ConfigureAwait(false),

            CliCommand.Hook =>
                await BookCommand.RunHookAsync(composition, directory, parsed.TicketId ?? 0,
                                               parsed.DryRun, parsed.Quiet, output, error, ct)
                    .ConfigureAwait(false),

            CliCommand.Book =>
                await BookCommand.RunBookAsync(composition, directory, parsed.Revision ?? "HEAD",
                                               parsed.TicketId ?? 0, parsed.DryRun, parsed.Quiet,
                                               output, error, ct).ConfigureAwait(false),

            CliCommand.Queue =>
                await QueueCommand.RunAsync(composition, parsed.Flush, output, ct).ConfigureAwait(false),

            CliCommand.Token =>
                await TokenCommand.RunAsync(composition, parsed.SubCommand ?? "status", output,
                                            error, ct).ConfigureAwait(false),

            CliCommand.Types =>
                await TypesCommand.RunAsync(composition, output, error, ct).ConfigureAwait(false),

            CliCommand.Log => LogCommand.Run(composition, parsed.Lines, output),

            _ => ExitCode.Usage,
        };
    }

    /// <summary>
    /// Was ohne Konfiguration noch geht.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Haken schweigt und endet mit 0.</b> Wer das Werkzeug deinstalliert oder die
    /// Konfiguration verschoben hat, soll nicht bei jedem Commit eine Fehlermeldung sehen — der
    /// Commit selbst ist in Ordnung. Auf der Fehlerausgabe steht eine Zeile, die sagt, warum
    /// nichts gebucht wurde.</para>
    /// <para><b><c>disable</c> geht trotzdem.</b> Einen Haken loszuwerden darf nicht davon
    /// abhängen, dass die Einrichtung heil ist — im Gegenteil, genau dann will man ihn los.</para>
    /// </remarks>
    private static async Task<int> WithoutConfigurationAsync(CliArgs parsed, ConfigStore store,
                                                             string directory, string? reason,
                                                             TextWriter output, TextWriter error,
                                                             CancellationToken ct)
    {
        if (parsed.Command == CliCommand.Hook)
        {
            if (!parsed.Quiet)
            {
                error.WriteLine("TANSS · nicht gebucht: keine Konfiguration unter " + store.Path
                    + " (tanss-git setup).");
            }

            return ExitCode.Healthy;
        }

        if (parsed.Command == CliCommand.Disable)
        {
            return await HookWithoutConfigurationAsync(directory, parsed.Global, output, error, ct)
                .ConfigureAwait(false);
        }

        HelpText.WriteMissingConfiguration(error, store.Path,
            reason ?? "Die Konfiguration liess sich nicht laden.");

        return ExitCode.Broken;
    }

    /// <summary>Entfernt den Haken, ohne dass eine Konfiguration vorliegen muss.</summary>
    private static async Task<int> HookWithoutConfigurationAsync(string directory, bool global,
                                                                 TextWriter output,
                                                                 TextWriter error,
                                                                 CancellationToken ct)
    {
        Git.IGitRunner git = new Git.GitRunner();
        Git.HookInstaller installer = new(git);

        try
        {
            if (global)
            {
                Git.HookStatus removed = Git.HookInstaller.Remove(Path.Combine(
                    StoragePaths.TemplateDirectory, Git.HookInstaller.TemplateHooksFolder));

                _ = await installer.UnsetTemplateDirectoryAsync(StoragePaths.TemplateDirectory, ct)
                    .ConfigureAwait(false);

                output.WriteLine("Die Vorlage ist entfernt: " + removed.Path);
                return ExitCode.Healthy;
            }

            Git.HookStatus before = await installer.InspectRepositoryAsync(directory, ct)
                .ConfigureAwait(false);

            if (before.State != Git.HookState.Ours)
            {
                output.WriteLine(before.State == Git.HookState.Missing
                    ? "Hier liegt kein Haken. Es war nichts zu tun."
                    : "Dort liegt ein fremder Haken. Er bleibt unangetastet: " + before.Path);

                return ExitCode.Healthy;
            }

            _ = await installer.DisableRepositoryAsync(directory, ct).ConfigureAwait(false);
            output.WriteLine("Der Haken ist entfernt: " + before.Path);
            return ExitCode.Healthy;
        }
        catch (Git.GitException exception)
        {
            error.WriteLine(exception.Message);
            return ExitCode.Broken;
        }
    }

    /// <summary>Lädt die Konfiguration, ohne zu werfen.</summary>
    /// <returns>Die Konfiguration, oder <c>null</c> samt Begründung in <paramref name="reason"/>.</returns>
    private static AppConfig? TryLoad(ConfigStore store, out string? reason)
    {
        try
        {
            AppConfig config = store.Load();
            reason = null;
            return config;
        }
        catch (StorageException exception)
        {
            // ConfigException und ConfigValidationException sind beide StorageException: Fuer
            // den Aufrufer macht es keinen Unterschied, ob die Datei fehlt, kaputt ist oder eine
            // Regel verletzt - er soll dieselbe Anleitung bekommen, mit dem jeweiligen Grund.
            reason = exception.Message;
            return null;
        }
    }

    /// <summary>
    /// Stellt die Konsole auf UTF-8.
    /// </summary>
    /// <remarks>
    /// Ohne das erscheinen Umlaute in der klassischen Eingabeaufforderung als Kästchen. Der
    /// Versuch darf scheitern — bei umgeleiteter Ausgabe gibt es keine Konsole, und daran soll
    /// kein Befehl hängenbleiben.
    /// </remarks>
    private static void UseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
