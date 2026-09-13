using System.Globalization;
using TanssGitConnector.Cli.Booking;
using TanssGitConnector.Cli.Output;
using TanssGitConnector.Storage.Queue;

namespace TanssGitConnector.Cli.Commands;

/// <summary>
/// Zeigt die Warteschlange und sendet sie auf Anforderung: <c>tanss-git queue [--flush]</c>.
/// </summary>
/// <remarks>
/// <b>Ohne <c>--flush</c> wird nichts gesendet.</b> Das ist die Trennung, die diesen Befehl
/// gefahrlos macht: Nachsehen darf jeder jederzeit, auch mitten in einer Störung; gesendet wird
/// nur, wenn jemand es sagt.
/// </remarks>
internal static class QueueCommand
{
    /// <summary>Führt den Befehl aus.</summary>
    /// <param name="composition">Die Bausteine dieses Laufs.</param>
    /// <param name="flush">Fällige Einträge senden.</param>
    /// <param name="output">Die Ausgabe.</param>
    /// <param name="ct">Abbruchmarke.</param>
    public static async Task<int> RunAsync(Composition composition, bool flush, TextWriter output,
                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(output);

        if (flush)
        {
            // Vor dem Senden die aufgegebenen Eintraege wieder aufnehmen: Wer "--flush" tippt,
            // will sie loswerden - sonst haette er nachgesehen und nichts weiter getan.
            int revived = composition.Outbox.Revive();
            if (revived > 0)
            {
                output.WriteLine($"{revived} aufgegebene(r) Eintrag/Einträge wieder aufgenommen.");
            }

            CommitUploader uploader = new(composition.RemoteSupports, composition.Outbox,
                                          composition.Log);

            FlushReport report = await uploader.FlushAsync(ct).ConfigureAwait(false);

            output.WriteLine(report.Total == 0
                ? "Es war nichts fällig."
                : $"Gebucht: {report.Sent}. Stand schon in TANSS: {report.AlreadyThere}. "
                  + $"Zurückgestellt: {report.Postponed}. Gescheitert: {report.Failed}.");
            output.WriteLine();

            Prune(composition, output);
        }

        return Show(composition, output);
    }

    /// <summary>Zeigt die Warteschlange und liefert den Befund als Rückgabewert.</summary>
    private static int Show(Composition composition, TextWriter output)
    {
        IReadOnlyList<QueuedCommit> entries = composition.Outbox.All();

        if (entries.Count == 0)
        {
            output.WriteLine("Die Warteschlange ist leer.");
            return ExitCode.Healthy;
        }

        int waiting = entries.Count(entry => entry.State == QueueState.Pending);
        int failed = entries.Count(entry => entry.State == QueueState.Failed);
        int done = entries.Count(entry => entry.State == QueueState.Done);

        output.WriteLine($"{entries.Count} Eintrag/Einträge — wartend {waiting}, aufgegeben "
            + $"{failed}, erledigt {done}.");
        output.WriteLine();
        output.WriteLine("Zustand     Commit   Repository       Ticket  Fällig               Betreff");

        foreach (QueuedCommit entry in entries.OrderBy(item => item.CreatedAt))
        {
            string ticket = entry.Payload.TicketId > 0
                ? entry.Payload.TicketId.ToString(CultureInfo.InvariantCulture)
                : "—";

            string due = entry.State == QueueState.Done
                ? entry.CompletedAt is { } completed ? Report.Moment(completed) : "—"
                : Report.Moment(entry.NextAttemptAt);

            output.WriteLine(
                $"{State(entry),-11} {entry.ShortSha,-8} {Report.Ellipsis(entry.Repository, 16),-16} "
                + $"{ticket,-7} {due,-20} {Report.Ellipsis(entry.Subject, 40)}");

            if (entry.State != QueueState.Done && entry.LastError is { Length: > 0 } problem)
            {
                foreach (string line in Report.Wrap(problem, 76))
                {
                    output.WriteLine("            " + line);
                }
            }
        }

        // Ein ungeklaerter Ausgang ist keine Warnung wert, solange der Eintrag wartet: Genau
        // dafuer gibt es die Existenzpruefung, und sie laeuft von selbst.
        if (failed > 0)
        {
            output.WriteLine();
            output.WriteLine("Aufgegebene Einträge tragen ungebuchte Arbeitszeit. Erneut versuchen "
                + "mit: tanss-git queue --flush");
            return ExitCode.Broken;
        }

        return waiting > 0 ? ExitCode.Warning : ExitCode.Healthy;
    }

    /// <summary>
    /// Räumt Erledigtes und altes Protokoll fort.
    /// </summary>
    /// <remarks>
    /// Hier und im Doktor, nicht im Hook: Das Aufräumen schreibt beide Dateien neu, und der
    /// Hook soll der schnellste Weg bleiben. Wer nie aufräumt, hat eine wachsende Datei —
    /// wer bei jedem Commit aufräumt, wartet bei jedem Commit darauf.
    /// </remarks>
    internal static void Prune(Composition composition, TextWriter output)
    {
        TimeSpan retention = TimeSpan.FromDays(composition.Config.Logging.RetentionDays);

        int entries = composition.Outbox.Prune(retention);
        int lines = composition.Log.Prune(retention);

        if (entries + lines > 0)
        {
            output.WriteLine($"Aufgeräumt: {entries} erledigte(r) Eintrag/Einträge, {lines} "
                + $"Protokollzeile(n) älter als {composition.Config.Logging.RetentionDays} Tage.");
            output.WriteLine();
        }
    }

    private static string State(QueuedCommit entry) => entry.State switch
    {
        QueueState.Done => "erledigt",
        QueueState.Failed => "aufgegeben",
        _ => entry.OutcomeUnknown ? "ungeklärt" : "wartend",
    };
}
