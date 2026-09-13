using TanssGitConnector.Api.Contract;
using TanssGitConnector.Api.Diagnostics;
using TanssGitConnector.Api.Model;
using TanssGitConnector.Git;
using TanssGitConnector.Storage.Config;
using TanssGitConnector.Storage.Logging;
using TanssGitConnector.Storage.Queue;

namespace TanssGitConnector.Cli.Booking;

/// <summary>Wie es einem Commit auf dem Weg in die Warteschlange ergangen ist.</summary>
public enum BookOutcome
{
    /// <summary>Eingereiht. Gesendet wird danach, in einem eigenen Schritt.</summary>
    Queued,

    /// <summary>Stand schon in der Warteschlange. Es wurde nichts angehängt.</summary>
    AlreadyQueued,

    /// <summary>Übergangen — mit Grund. Kein Fehler.</summary>
    Skipped,

    /// <summary>Nur gezeigt, nichts eingereiht (<c>--dry-run</c>).</summary>
    Planned,
}

/// <summary>Das Ergebnis eines Buchungsversuchs.</summary>
/// <param name="Outcome">Wie es ausgegangen ist.</param>
/// <param name="Commit">Der Commit, um den es ging.</param>
/// <param name="Payload">Die Fernwartung, sofern eine gebaut wurde.</param>
/// <param name="Ticket">Die gefundene Ticketnummer samt Herkunft.</param>
/// <param name="Reason">Der Grund — bei <see cref="BookOutcome.Skipped"/> immer gesetzt.</param>
/// <param name="TicketNote">
/// Was die Ticketprüfung ergeben hat, sofern sie lief und etwas zu sagen hatte.
/// </param>
public sealed record BookResult(BookOutcome Outcome, CommitInfo Commit,
                                RemoteSupportWrite? Payload, TicketReference Ticket,
                                string? Reason = null, string? TicketNote = null);

/// <summary>
/// Macht aus einem Commit einen Eintrag in der Warteschlange.
/// </summary>
/// <remarks>
/// <para><b>Eingereiht wird, bevor irgendetwas ins Netz geht.</b> Das ist die Reihenfolge, an
/// der die Verlustsicherheit hängt: Ein Netzausfall verschiebt die Buchung dann, statt sie zu
/// kosten.</para>
/// <para><b>Ein übergangener Commit wird protokolliert.</b> In TANSS kommt bei ihm nie etwas an;
/// das Protokoll auf diesem Rechner ist der einzige Ort, an dem später steht, warum die
/// Arbeitszeit fehlt.</para>
/// </remarks>
internal sealed class CommitBooker
{
    private readonly AppConfig _config;
    private readonly GitRepository _repository;
    private readonly CommitOutbox _outbox;
    private readonly ActivityLog _log;
    private readonly ITicketVerification? _tickets;

    /// <summary>Baut die Buchung.</summary>
    /// <param name="config">Die Konfiguration.</param>
    /// <param name="repository">Der lesende Zugriff auf Git.</param>
    /// <param name="outbox">Die Warteschlange.</param>
    /// <param name="log">Das Änderungsprotokoll.</param>
    /// <param name="tickets">
    /// Die Ticketprüfung, oder <see langword="null"/>. Fehlt sie, wird die Nummer ungeprüft
    /// übernommen — das ist der Fall bei <c>--dry-run</c> ohne Netz.
    /// </param>
    public CommitBooker(AppConfig config, GitRepository repository, CommitOutbox outbox,
                        ActivityLog log, ITicketVerification? tickets = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(log);

        _config = config;
        _repository = repository;
        _outbox = outbox;
        _log = log;
        _tickets = tickets;
    }

    /// <summary>
    /// Liest einen Commit und reiht ihn ein.
    /// </summary>
    /// <param name="directory">Ein Verzeichnis innerhalb des Repositorys.</param>
    /// <param name="revision">Die Version; im Hook <c>HEAD</c>.</param>
    /// <param name="explicitTicketId">Eine von Hand angegebene Ticketnummer, oder 0.</param>
    /// <param name="dryRun">Nur zeigen, nichts einreihen.</param>
    /// <param name="ct">Abbruchmarke.</param>
    public async Task<BookResult> BookAsync(string directory, string revision = "HEAD",
                                            int explicitTicketId = 0, bool dryRun = false,
                                            CancellationToken ct = default)
    {
        CommitInfo commit = await _repository.ReadCommitAsync(directory, revision, ct)
            .ConfigureAwait(false);

        if (_config.Commits.SkipMergeCommits && commit.IsMerge)
        {
            return Skip(commit, TicketReference.None,
                "Zusammenführungs-Commit übergangen. Seine Arbeitszeit steckt in den Commits, "
                + "die er zusammenführt, und die sind bereits gebucht. Zu ändern über "
                + "commits.skip_merge_commits.");
        }

        TicketReference ticket = TicketNumbers.Resolve(commit, explicitTicketId,
            _config.Tickets.FromBranch, _config.Tickets.FromMessage);

        if (_config.Commits.OnlyWithTicket && !ticket.HasTicket)
        {
            return Skip(commit, ticket,
                "Ohne Ticketnummer wird nicht gebucht (commits.only_with_ticket). Der Zweig "
                + "endet nicht auf #<Nummer>, und die Commit-Meldung trägt keine Zeile "
                + "„Ticket: <Nummer>“.");
        }

        (int ticketId, string? ticketNote) = await VerifyAsync(ticket, ct).ConfigureAwait(false);

        DateTimeOffset? previous = _config.Commits.DurationMode == DurationMode.SinceLastCommit
            ? await _repository.PreviousCommitTimeAsync(directory, commit.Sha, ct).ConfigureAwait(false)
            : null;

        RemoteSupportWrite payload = CommitBooking.Build(commit, previous, ticketId,
                                                         _config.ToBookingSettings());

        if (dryRun)
        {
            return new BookResult(BookOutcome.Planned, commit, payload, ticket, null, ticketNote);
        }

        EnqueueResult enqueued = _outbox.Enqueue(payload, commit.RepositoryName, commit.Subject);

        if (!enqueued.Added)
        {
            // Derselbe Commit ein zweites Mal - nach einem zweiten Hook, einem Nachreichen von
            // Hand oder einem wiederholten Lauf. Nicht anhaengen: Der Hash ist der Schluessel,
            // und TANSS dedupliziert nicht.
            _log.Info("book.duplicate",
                "Dieser Commit steht bereits in der Warteschlange oder ist gebucht. Es wurde "
                + "nichts angehängt.", commit.Sha, commit.RepositoryName, ticketId);

            return new BookResult(BookOutcome.AlreadyQueued, commit, payload, ticket,
                "Steht bereits in der Warteschlange.", ticketNote);
        }

        _log.Info("book.queued",
            $"Eingereiht: {CommitBooking.Origin(commit, _config.ToBookingSettings())}, "
            + $"{ticket.Describe()}.", commit.Sha, commit.RepositoryName, ticketId);

        return new BookResult(BookOutcome.Queued, commit, payload, ticket, null, ticketNote);
    }

    /// <summary>
    /// Prüft die Ticketnummer, wenn das eingeschaltet ist.
    /// </summary>
    /// <remarks>
    /// <b>Angehalten wird nur bei einem belegten „gibt es nicht“.</b> Eine misslungene Prüfung
    /// lässt die Nummer stehen: Sie ist kein Beleg gegen das Ticket, und ein weggelassener
    /// Ticketbezug wäre eine Fernwartung, die beim Kunden niemand zuordnen kann.
    /// </remarks>
    /// <returns>Die zu buchende Nummer und ein Hinweistext, sofern es etwas zu sagen gibt.</returns>
    private async Task<(int TicketId, string? Note)> VerifyAsync(TicketReference ticket,
                                                                 CancellationToken ct)
    {
        if (!ticket.HasTicket || !_config.Tickets.Verify || _tickets is null)
        {
            return (ticket.TicketId, null);
        }

        TicketCheck check;
        try
        {
            check = await _tickets.CheckAsync(ticket.TicketId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Die Pruefung darf den Commit niemals kosten - auch dann nicht, wenn sie an etwas
            // scheitert, das sie selbst nicht vorhergesehen hat: fehlendes Token, unlesbarer
            // Speicher, kaputte Ablage. Der Commit wird mit Ticketbezug eingereiht, und woran
            // es lag, steht im Protokoll.
            string note = "Die Ticketprüfung liess sich nicht durchführen: "
                + Redaction.Scrub(exception.Message);

            _log.Warning("book.ticket-unchecked", note, null, null, ticket.TicketId);
            return (ticket.TicketId, note);
        }

        if (!check.MayLink)
        {
            // Der einzige Ausgang, der den Ticketbezug kostet: ein belegtes "gibt es nicht".
            _log.Warning("book.ticket-missing", check.Explanation, null, null, ticket.TicketId);
            return (0, check.Explanation);
        }

        return (ticket.TicketId, check.Outcome == TicketCheckOutcome.Exists
            ? check.Summary
            : check.Explanation);
    }

    private BookResult Skip(CommitInfo commit, TicketReference ticket, string reason)
    {
        _log.Info("book.skipped", reason, commit.Sha, commit.RepositoryName, ticket.TicketId);
        return new BookResult(BookOutcome.Skipped, commit, null, ticket, reason);
    }
}
