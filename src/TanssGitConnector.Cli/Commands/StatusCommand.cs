using TanssGitConnector.Api;
using TanssGitConnector.Api.Auth;
using TanssGitConnector.Cli.Output;
using TanssGitConnector.Git;
using TanssGitConnector.Storage;
using TanssGitConnector.Storage.Queue;
using TanssGitConnector.Storage.Secrets;

namespace TanssGitConnector.Cli.Commands;

/// <summary>
/// Zeigt, was auf diesem Rechner eingerichtet ist: <c>tanss-git status</c>.
/// </summary>
/// <remarks>
/// <b>Ohne einen einzigen Netzzugriff.</b> Das ist der Unterschied zu <c>doctor</c>: Dieser
/// Befehl beantwortet „was ist hier eingerichtet?“ und antwortet auch dann, wenn die Instanz
/// gerade nicht erreichbar ist. Wer wissen will, ob die Einrichtung <i>funktioniert</i>, nimmt
/// den Doktor.
/// </remarks>
internal static class StatusCommand
{
    /// <summary>Führt den Befehl aus.</summary>
    public static async Task<int> RunAsync(Composition composition, string configPath,
                                           string directory, TextWriter output,
                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine($"tanss-git {CommandLine.HelpText.Version}");
        output.WriteLine();
        output.WriteLine("Ablage");
        output.WriteLine("  Konfiguration   " + configPath);
        output.WriteLine("  Zustand         " + StoragePaths.StateDirectory);
        output.WriteLine("  Warteschlange   " + composition.Outbox.Path);
        output.WriteLine("  Protokoll       " + composition.Log.Path);
        output.WriteLine();
        output.WriteLine("TANSS");
        output.WriteLine("  Instanz         " + composition.Config.Tanss.BaseUrl);
        output.WriteLine("  Mitarbeiter     " + composition.Config.Tanss.EmployeeId);
        output.WriteLine("  Anbindung       " + composition.Config.Commits.RemoteSupportTypeId);
        output.WriteLine("  Token           " + DescribeToken(composition));
        output.WriteLine();
        output.WriteLine("Buchung");
        output.WriteLine("  Dauer           " + DescribeDuration(composition));
        output.WriteLine("  Ticket          " + DescribeTickets(composition));
        output.WriteLine("  Merge-Commits   " + (composition.Config.Commits.SkipMergeCommits
            ? "werden übergangen" : "werden gebucht"));
        output.WriteLine("  Ohne Ticket     " + (composition.Config.Commits.OnlyWithTicket
            ? "wird nicht gebucht" : "wird gebucht"));
        output.WriteLine();
        output.WriteLine("Haken");

        foreach (string line in await DescribeHooksAsync(composition, directory, ct).ConfigureAwait(false))
        {
            output.WriteLine("  " + line);
        }

        output.WriteLine();
        output.WriteLine("Warteschlange");
        output.WriteLine("  " + DescribeQueue(composition));

        return ExitCode.Healthy;
    }

    private static string DescribeToken(Composition composition)
    {
        try
        {
            DateTimeOffset? expiry = TanssAuth.DecodeClaims(composition.Tokens.Read()).ExpiresAt;

            string protection = TokenProtection.DescribeProtection();

            return expiry is { } moment
                ? $"gültig bis {Report.Moment(moment)} ({Report.Duration(moment - DateTimeOffset.UtcNow)}) — {protection}"
                : $"ohne Ablaufangabe — {protection}";
        }
        catch (StorageException exception)
        {
            return "fehlt — " + Report.Ellipsis(exception.Message, 90);
        }
        catch (TanssAuthException exception)
        {
            return "unlesbar — " + Report.Ellipsis(exception.Message, 90);
        }
    }

    private static string DescribeDuration(Composition composition) =>
        composition.Config.Commits.DurationMode == DurationMode.Fixed
            ? $"fest {composition.Config.Commits.DurationMinutes} Minuten je Commit"
            : $"seit dem letzten Commit, mindestens {composition.Config.Commits.MinimumMinutes}, "
              + $"höchstens {composition.Config.Commits.MaximumMinutes} Minuten";

    private static string DescribeTickets(Composition composition)
    {
        List<string> sources = [];

        if (composition.Config.Tickets.FromBranch)
        {
            sources.Add("Zweigname endet auf #<Nummer>");
        }

        if (composition.Config.Tickets.FromMessage)
        {
            sources.Add("Zeile „Ticket: <Nummer>“ in der Meldung");
        }

        string where = sources.Count > 0 ? string.Join(" oder ", sources) : "keine Quelle aktiv";
        return where + (composition.Config.Tickets.Verify ? "; wird vor dem Buchen geprüft" : "");
    }

    private static async Task<IReadOnlyList<string>> DescribeHooksAsync(Composition composition,
                                                                        string directory,
                                                                        CancellationToken ct)
    {
        List<string> lines = [];

        try
        {
            HookStatus here = await composition.Hooks.InspectRepositoryAsync(directory, ct)
                .ConfigureAwait(false);

            lines.Add("Dieses Repository  " + here.State switch
            {
                HookState.Ours => "eingerichtet — " + here.Path,
                HookState.Foreign => "fremder Haken, unangetastet — " + here.Path,
                HookState.Unreadable => "unlesbar — " + here.Path,
                _ => "kein Haken (tanss-git enable)",
            });
        }
        catch (NotARepositoryException)
        {
            lines.Add("Dieses Repository  kein Git-Repository");
        }
        catch (GitException exception)
        {
            lines.Add("Dieses Repository  nicht feststellbar — " + Report.Ellipsis(exception.Message, 70));
        }

        HookStatus template = HookInstaller.Inspect(
            Path.Combine(StoragePaths.TemplateDirectory, HookInstaller.TemplateHooksFolder));

        string? configured = await composition.Hooks.ConfiguredTemplateDirectoryAsync(ct)
            .ConfigureAwait(false);

        lines.Add("Neue Repositorys   " + (template.IsInstalled
            ? configured is { Length: > 0 }
              && HookInstaller.SamePath(configured, StoragePaths.TemplateDirectory)
                ? "Vorlage aktiv — " + template.Path
                : "Vorlage vorhanden, aber init.templatedir zeigt woandershin: "
                  + (configured ?? "nicht gesetzt")
            : "keine Vorlage (tanss-git enable --global)"));

        return lines;
    }

    private static string DescribeQueue(Composition composition)
    {
        try
        {
            IReadOnlyList<QueuedCommit> entries = composition.Outbox.All();

            return entries.Count == 0
                ? "leer"
                : $"{entries.Count(entry => entry.State == QueueState.Pending)} wartend, "
                  + $"{entries.Count(entry => entry.State == QueueState.Failed)} aufgegeben, "
                  + $"{entries.Count(entry => entry.State == QueueState.Done)} erledigt";
        }
        catch (StorageException exception)
        {
            return "nicht lesbar — " + Report.Ellipsis(exception.Message, 90);
        }
    }
}
