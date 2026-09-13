using TanssGitConnector.Api;
using TanssGitConnector.Api.Diagnostics;
using TanssGitConnector.Api.Model;
using TanssGitConnector.Cli.Booking;
using TanssGitConnector.Cli.Output;
using TanssGitConnector.Git;
using TanssGitConnector.Storage.Queue;

namespace TanssGitConnector.Cli.Commands;

/// <summary>
/// Bucht einen Commit: <c>tanss-git hook</c> und <c>tanss-git book</c>.
/// </summary>
/// <remarks>
/// <para>Beide Befehle tun dasselbe und unterscheiden sich in drei Punkten: Der Haken nimmt
/// immer <c>HEAD</c>, er gibt höchstens eine Zeile aus, und er endet <b>immer</b> mit 0.</para>
///
/// <para><b>Warum der Haken nie scheitert.</b> Wenn er läuft, ist der Commit bereits
/// geschrieben. Ein Rückgabewert ungleich 0 sähe für den Techniker nach einem misslungenen
/// Commit aus und verleitete zu einem <c>--amend</c> — das erzeugt einen neuen Hash, und der
/// alte bliebe als eigener Eintrag in der Warteschlange liegen.</para>
/// </remarks>
internal static class BookCommand
{
    /// <summary>Führt <c>hook</c> aus: HEAD buchen, kurz melden, immer mit 0 enden.</summary>
    /// <param name="composition">Die Bausteine dieses Laufs.</param>
    /// <param name="directory">Das Repository.</param>
    /// <param name="explicitTicketId">Eine von Hand angegebene Ticketnummer, oder 0.</param>
    /// <param name="dryRun">Nur zeigen, nichts einreihen.</param>
    /// <param name="quiet">Nichts ausgeben, wenn alles gutgegangen ist.</param>
    /// <param name="output">Die gewöhnliche Ausgabe.</param>
    /// <param name="error">Die Fehlerausgabe.</param>
    /// <param name="ct">Abbruchmarke.</param>
    public static async Task<int> RunHookAsync(Composition composition, string directory,
                                               int explicitTicketId, bool dryRun, bool quiet,
                                               TextWriter output, TextWriter error,
                                               CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            await RunAsync(composition, directory, "HEAD", explicitTicketId, dryRun,
                           quiet || composition.Config.Hook.Quiet, compact: true, output, error, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("TANSS · abgebrochen. Der Commit bleibt in der Warteschlange.");
        }
        catch (Exception exception)
        {
            // Der Riegel, der die Zusage haelt: Was auch immer hier ankommt - ein kaputtes
            // Repository, eine gesperrte Datei, ein Programmfehler -, der Commit selbst ist
            // davon unberuehrt und soll nicht als misslungen erscheinen.
            error.WriteLine("TANSS · nicht gebucht: " + Redaction.Scrub(exception.Message));

            // HEAD und kein Hash: Ist das Lesen des Commits gescheitert, gibt es keinen Hash,
            // den man nennen koennte - und ein erfundener waere schlimmer als keiner.
            error.WriteLine("        Der Commit ist davon unberührt. Nachreichen mit: "
                + "tanss-git book HEAD");
        }

        return ExitCode.Healthy;
    }

    /// <summary>Führt <c>book</c> aus: eine benannte Fassung buchen, ausführlich melden.</summary>
    public static Task<int> RunBookAsync(Composition composition, string directory,
                                         string revision, int explicitTicketId, bool dryRun,
                                         bool quiet, TextWriter output, TextWriter error,
                                         CancellationToken ct = default) =>
        RunAsync(composition, directory, revision, explicitTicketId, dryRun, quiet,
                 compact: false, output, error, ct);

    private static async Task<int> RunAsync(Composition composition, string directory,
                                            string revision, int explicitTicketId, bool dryRun,
                                            bool quiet, bool compact, TextWriter output,
                                            TextWriter error, CancellationToken ct)
    {
        CommitBooker booker = new(composition.Config, composition.Repository, composition.Outbox,
                                  composition.Log, composition.Tickets);

        BookResult result;
        try
        {
            result = await booker.BookAsync(directory, revision, explicitTicketId, dryRun, ct)
                .ConfigureAwait(false);
        }
        catch (NotARepositoryException exception)
        {
            error.WriteLine(exception.Message);
            return ExitCode.Broken;
        }
        catch (GitException exception)
        {
            error.WriteLine(exception.Message);
            return ExitCode.Broken;
        }

        if (result.Outcome == BookOutcome.Skipped)
        {
            if (!quiet)
            {
                output.WriteLine(compact
                    ? "TANSS · übergangen: " + Report.Ellipsis(result.Reason, 100)
                    : "Übergangen: " + result.Reason);
            }

            return ExitCode.Healthy;
        }

        if (result.Outcome == BookOutcome.Planned)
        {
            WritePlan(output, result);
            return ExitCode.Healthy;
        }

        if (result.Outcome == BookOutcome.AlreadyQueued)
        {
            if (!quiet)
            {
                output.WriteLine(compact
                    ? $"TANSS · {result.Commit.ShortSha} steht bereits in der Warteschlange."
                    : $"Der Commit {result.Commit.ShortSha} steht bereits in der Warteschlange "
                      + "oder ist gebucht. Es wurde nichts angehängt.");
            }

            return ExitCode.Healthy;
        }

        return await SendAsync(composition, result, quiet, compact, output, error, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Schickt den soeben eingereihten Commit los — mit der Zeitgrenze des Hakens.
    /// </summary>
    /// <remarks>
    /// <b>Die Zeitgrenze ist die Zeit, die der Techniker wartet.</b> Läuft sie ab, bleibt der
    /// Eintrag in der Warteschlange und geht beim nächsten Commit oder beim nächsten
    /// <c>queue --flush</c> mit. Verloren ist nichts — deshalb darf diese Grenze knapp sein.
    /// </remarks>
    private static async Task<int> SendAsync(Composition composition, BookResult result,
                                             bool quiet, bool compact, TextWriter output,
                                             TextWriter error, CancellationToken ct)
    {
        string line = Describe(result, composition);

        if (!composition.Config.Hook.SendImmediately)
        {
            if (!quiet)
            {
                output.WriteLine(line + " · eingereiht");
                if (!compact)
                {
                    output.WriteLine("Gesendet wird auf Anforderung: tanss-git queue --flush");
                }
            }

            return ExitCode.Healthy;
        }

        CommitUploader uploader = new(composition.RemoteSupports, composition.Outbox,
                                      composition.Log);

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(composition.Config.Hook.TimeoutSeconds));

        QueuedCommit? entry = composition.Outbox.Find(result.Commit.Sha);
        if (entry is null)
        {
            // Kann nur passieren, wenn jemand die Warteschlange zwischen Einreihen und Senden
            // von Hand angefasst hat. Kein Fehler, aber auch nichts zu senden.
            return ExitCode.Healthy;
        }

        UploadOutcome outcome;
        try
        {
            outcome = await uploader.SendAsync(entry, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Die eigene Zeitgrenze. Den Eintrag hat CommitUploader.SendAsync bereits als
            // ungeklaert vermerkt - hier wird deshalb NICHT ein zweites Mal Fail aufgerufen:
            // Das zaehlte den Versuch doppelt und verdoppelte den Rueckstau.
            composition.Log.Warning("hook.timeout",
                "Die Zeitgrenze des Hakens ist abgelaufen, bevor TANSS geantwortet hat. Der "
                + "Commit bleibt in der Warteschlange; vor der Wiederholung wird geprüft, ob er "
                + "doch angekommen ist.", entry.RemoteMaintenanceId, entry.Repository);

            if (!quiet)
            {
                error.WriteLine(line + " · eingereiht — ob TANSS die Buchung angelegt hat, ist "
                    + "offen; es wird geprüft, bevor erneut gesendet wird");
            }

            return ExitCode.Warning;
        }

        if (!quiet || outcome is UploadOutcome.Failed or UploadOutcome.Postponed)
        {
            TextWriter writer = outcome is UploadOutcome.Sent or UploadOutcome.AlreadyThere
                ? output
                : error;

            writer.WriteLine(line + " · " + Describe(outcome));
        }

        return outcome switch
        {
            UploadOutcome.Sent or UploadOutcome.AlreadyThere => ExitCode.Healthy,

            // Eingereiht, aber nicht angekommen: keine verlorene Arbeitszeit, aber auch kein
            // erledigter Vorgang. Genau dafuer gibt es die Stufe dazwischen.
            _ => ExitCode.Warning,
        };
    }

    private static void WritePlan(TextWriter output, BookResult result)
    {
        RemoteSupportWrite payload = result.Payload
            ?? throw new InvalidOperationException("Ein Probelauf ohne Nutzlast ist ein Programmfehler.");

        DateTimeOffset start = TanssTime.FromUnixSeconds(payload.StartTime) ?? DateTimeOffset.UtcNow;
        DateTimeOffset end = TanssTime.FromUnixSeconds(payload.EndTime) ?? DateTimeOffset.UtcNow;

        output.WriteLine("Probelauf — es wird nichts eingereiht und nichts gesendet.");
        output.WriteLine();
        output.WriteLine($"  Commit        {result.Commit.ShortSha} {result.Commit.Subject}");
        output.WriteLine($"  Repository    {result.Commit.RepositoryName}");
        output.WriteLine($"  Zweig         {(result.Commit.Branch.Length > 0 ? result.Commit.Branch : "— (abgetrennter HEAD)")}");
        output.WriteLine($"  Anbindung     {payload.TypeId}");
        output.WriteLine($"  Mitarbeiter   {payload.EmployeeId}");
        output.WriteLine($"  Zeitraum      {Report.Moment(start)} bis {Report.Moment(end)} ({Report.Duration(end - start)})");
        output.WriteLine($"  Ticket        {(payload.TicketId > 0 ? payload.TicketId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "keines")} — {result.Ticket.Describe()}");

        if (result.TicketNote is { Length: > 0 } note)
        {
            output.WriteLine($"                {note}");
        }

        output.WriteLine($"  Kennung       {payload.RemoteMaintenanceId}");
        output.WriteLine();
        output.WriteLine("  Kommentar:");

        foreach (string line in payload.Comment.ReplaceLineEndings("\n").Split('\n'))
        {
            output.WriteLine("    " + line);
        }
    }

    /// <summary>Die eine Zeile, die der Haken ausgibt.</summary>
    private static string Describe(BookResult result, Composition composition)
    {
        RemoteSupportWrite payload = result.Payload!;
        TimeSpan duration = TimeSpan.FromSeconds(payload.EndTime - payload.StartTime);

        string ticket = payload.TicketId > 0
            ? $"Ticket {payload.TicketId}"
            : "ohne Ticket";

        return $"TANSS · {result.Commit.ShortSha} „{Report.Ellipsis(result.Commit.Subject, 48)}“ "
            + $"· {Report.Duration(duration)} · {ticket}";
    }

    /// <summary>
    /// Der Ausgang in einem Satzteil.
    /// </summary>
    /// <remarks>
    /// Bei <see cref="UploadOutcome.Failed"/> steht hier bewusst <b>kein</b> Grund. Der kann
    /// alles sein — kein Token, kein Netz, eine abgewiesene Anfrage —, und ihn in dieser einen
    /// Zeile zu raten hiesse, den Techniker in die falsche Richtung zu schicken. Der genaue
    /// Grund steht in der Warteschlange und im Protokoll.
    /// </remarks>
    private static string Describe(UploadOutcome outcome) => outcome switch
    {
        UploadOutcome.Sent => "gebucht",
        UploadOutcome.AlreadyThere => "stand schon in TANSS",
        UploadOutcome.Postponed => "zurückgestellt — wird geprüft, bevor erneut gesendet wird",
        _ => "eingereiht, aber nicht gebucht (Grund: tanss-git queue)",
    };

}
