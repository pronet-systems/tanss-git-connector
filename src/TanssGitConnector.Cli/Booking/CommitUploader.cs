using TanssGitConnector.Api;
using TanssGitConnector.Api.Contract;
using TanssGitConnector.Api.Diagnostics;
using TanssGitConnector.Api.Model;
using TanssGitConnector.Storage;
using TanssGitConnector.Storage.Logging;
using TanssGitConnector.Storage.Queue;

namespace TanssGitConnector.Cli.Booking;

/// <summary>Wie ein einzelner Sendeversuch ausgegangen ist.</summary>
public enum UploadOutcome
{
    /// <summary>Angelegt. Der Eintrag ist erledigt.</summary>
    Sent,

    /// <summary>Die Fernwartung stand schon in TANSS. Der Eintrag ist erledigt, ohne zu senden.</summary>
    AlreadyThere,

    /// <summary>
    /// Zurückgestellt. Es wurde <b>nicht</b> gesendet.
    /// </summary>
    /// <remarks>
    /// Der wichtigste Ausgang: Er steht für „der Ausgang des letzten Versuchs ist ungeklärt, und
    /// die Existenzprüfung konnte ihn nicht klären“. Gesendet wird dann nicht — eine Dublette in
    /// TANSS ist nur über einen direkten Datenbankzugriff wieder zu entfernen.
    /// </remarks>
    Postponed,

    /// <summary>Der Versuch ist gescheitert. Der Eintrag bleibt in der Warteschlange.</summary>
    Failed,
}

/// <summary>Was ein Durchlauf durch die Warteschlange ergeben hat.</summary>
/// <param name="Sent">Wie viele angelegt wurden.</param>
/// <param name="AlreadyThere">Wie viele schon in TANSS standen.</param>
/// <param name="Postponed">Wie viele zurückgestellt wurden.</param>
/// <param name="Failed">Wie viele gescheitert sind.</param>
public sealed record FlushReport(int Sent, int AlreadyThere, int Postponed, int Failed)
{
    /// <summary>Wie viele Einträge angefasst wurden.</summary>
    public int Total => Sent + AlreadyThere + Postponed + Failed;

    /// <summary>Ist etwas liegengeblieben?</summary>
    public bool HasLeftovers => Postponed + Failed > 0;
}

/// <summary>
/// Bringt die Warteschlange nach TANSS.
/// </summary>
/// <remarks>
/// <para><b>Hier steht die Regel, deren Bruch am teuersten ist.</b> TANSS dedupliziert nicht:
/// Ein zweiter Aufruf mit derselben Kennung erzeugt einen zweiten Datensatz, und eine Dublette
/// ist nur über einen direkten Datenbankzugriff wieder zu entfernen. Deshalb gilt ohne
/// Ausnahme:</para>
///
/// <para><b>Vor jeder Wiederholung eines Eintrags mit ungeklärtem Ausgang steht die
/// Existenzprüfung.</b> Und der Umkehrschluss, den man leicht falsch macht: Scheitert die
/// Prüfung selbst, heißt das <i>unbekannt</i> und nicht <i>nicht vorhanden</i>. Der Eintrag wird
/// dann zurückgestellt, nicht gesendet. Wer diese Fehlerbehandlung „vereinfacht“ und im Zweifel
/// sendet, erzeugt Dubletten in der Produktivinstanz eines Kunden.</para>
///
/// <para><b>Ein Fehlschlag eines Eintrags beendet den Durchlauf nicht.</b> Die übrigen sind
/// eigene Vorgänge und haben mit ihm nichts zu tun.</para>
/// </remarks>
internal sealed class CommitUploader
{
    private readonly IRemoteSupportRepository _remoteSupports;
    private readonly CommitOutbox _outbox;
    private readonly ActivityLog _log;

    /// <summary>Baut den Versand.</summary>
    public CommitUploader(IRemoteSupportRepository remoteSupports, CommitOutbox outbox,
                          ActivityLog log)
    {
        ArgumentNullException.ThrowIfNull(remoteSupports);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(log);

        _remoteSupports = remoteSupports;
        _outbox = outbox;
        _log = log;
    }

    /// <summary>Sendet alle fälligen Einträge.</summary>
    /// <param name="ct">Abbruchmarke. Ein Abbruch beendet den Durchlauf, nicht den Eintrag.</param>
    public async Task<FlushReport> FlushAsync(CancellationToken ct = default)
    {
        int sent = 0, already = 0, postponed = 0, failed = 0;

        foreach (QueuedCommit entry in _outbox.Due())
        {
            ct.ThrowIfCancellationRequested();

            switch (await SendAsync(entry, ct).ConfigureAwait(false))
            {
                case UploadOutcome.Sent:
                    sent++;
                    break;
                case UploadOutcome.AlreadyThere:
                    already++;
                    break;
                case UploadOutcome.Postponed:
                    postponed++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        return new FlushReport(sent, already, postponed, failed);
    }

    /// <summary>
    /// Sendet einen Eintrag — oder stellt ihn zurück.
    /// </summary>
    /// <remarks>
    /// Wirft nicht. Jeder Ausgang landet in der Warteschlange und im Protokoll; ein geworfener
    /// Fehler würde den Aufrufer zur Wiederholung verleiten, und genau die erzeugt die Dublette.
    /// </remarks>
    public async Task<UploadOutcome> SendAsync(QueuedCommit entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.OutcomeUnknown)
        {
            UploadOutcome? decided = await CheckExistingAsync(entry, ct).ConfigureAwait(false);
            if (decided is { } outcome)
            {
                return outcome;
            }
        }

        try
        {
            RemoteSupportCreateResult created = await _remoteSupports
                .CreateWithDiagnosticsAsync(entry.Payload, ct).ConfigureAwait(false);

            _outbox.Complete(entry.RemoteMaintenanceId, created.Support.Id);

            if (created.Warning is { Length: > 0 } warning)
            {
                // Angelegt, aber die Zuordnung zum Techniker ist nicht bestaetigt. Kein Grund
                // zur Wiederholung - im Gegenteil: Der Datensatz ist da, ein zweiter waere eine
                // Dublette. Gemeldet wird trotzdem, sonst faellt es niemandem auf.
                _log.Warning("queue.attribution", warning, entry.RemoteMaintenanceId,
                             entry.Repository, entry.Payload.TicketId);
            }
            else
            {
                _log.Info("queue.sent",
                    $"Gebucht als Fernwartung {created.Support.Id}.",
                    entry.RemoteMaintenanceId, entry.Repository, entry.Payload.TicketId);
            }

            return UploadOutcome.Sent;
        }
        catch (OperationCanceledException)
        {
            // Strg+C oder eine Zeitgrenze MITTEN IM SENDEN. Der Abbruch sagt nichts darueber,
            // ob die Anfrage den Server erreicht hat - sie kann angekommen, verarbeitet und nur
            // die Antwort nie gelesen worden sein. Ohne diesen Zweig bliebe der Eintrag als
            // scheinbar geklaerter Erstversuch stehen, und die naechste Wiederholung ginge ohne
            // Existenzpruefung hinaus.
            _outbox.Fail(entry.RemoteMaintenanceId,
                "Abgebrochen, waehrend die Anfrage unterwegs war.", outcomeUnknown: true);

            _log.Warning("queue.cancelled",
                "Der Sendevorgang wurde abgebrochen. Ob TANSS die Fernwartung angelegt hat, ist "
                + "offen; vor der Wiederholung wird geprüft.",
                entry.RemoteMaintenanceId, entry.Repository, entry.Payload.TicketId);

            // Weiterreichen: Ein verschluckter Abbruch ist schlimmer als ein durchgereichter.
            throw;
        }
        catch (TanssUnreachableException exception)
        {
            // Der gefaehrliche Fall: Die Anfrage kann angekommen sein, nur die Antwort fehlt.
            // Ab jetzt gilt dieser Eintrag als ungeklaert, und vor jeder Wiederholung steht
            // die Existenzpruefung.
            Fail(entry, exception, outcomeUnknown: true, "queue.unreachable");
            return UploadOutcome.Failed;
        }
        catch (TanssException exception)
        {
            // NICHT jede Antwort klaert den Ausgang. Die Unterscheidung haengt am Status:
            //
            //   4xx  - TANSS hat die Anfrage geprueft und abgelehnt. Nichts angelegt, geklaert.
            //   5xx  - Der Server ist auf halbem Weg gestolpert. Ob er die Fernwartung vorher
            //          geschrieben hat, weiss von hier aus niemand.
            //   kein Status - die Anfrage war erfolgreich (2xx), und erst danach ging etwas
            //          schief: kein Datensatz im Rumpf, kein JSON, ein unerwartetes Modell. Das
            //          ist der sicherste Hinweis darauf, dass TANSS sehr wohl etwas angelegt hat.
            //
            // Frueher galt hier jede Ausnahme ausser der Unerreichbarkeit als "geklaert: nichts
            // angelegt". Damit waere ausgerechnet der Fall, in dem der Datensatz am
            // wahrscheinlichsten schon steht, ohne Existenzpruefung wiederholt worden.
            bool unknown = exception.Status is null or (>= 500 and < 600);

            Fail(entry, exception, unknown, unknown ? "queue.unclear" : "queue.rejected");
            return UploadOutcome.Failed;
        }
        catch (StorageException exception)
        {
            // Es ging gar nichts hinaus: Ohne lesbares Token wird keine Anfrage gebaut. Der
            // Eintrag bleibt liegen, der Ausgang ist geklaert, und die Meldung nennt die
            // Einrichtung - nicht die Instanz, die hier unschuldig ist.
            Fail(entry, exception, outcomeUnknown: false, "queue.not-configured");
            return UploadOutcome.Failed;
        }
    }

    /// <summary>
    /// Klärt für einen Eintrag mit ungeklärtem Ausgang, ob er schon in TANSS steht.
    /// </summary>
    /// <returns>
    /// Der entschiedene Ausgang — oder <see langword="null"/>, wenn gesendet werden darf.
    /// </returns>
    private async Task<UploadOutcome?> CheckExistingAsync(QueuedCommit entry, CancellationToken ct)
    {
        try
        {
            bool exists = await _remoteSupports.ExistsAsync(entry.Payload, ct).ConfigureAwait(false);

            if (!exists)
            {
                return null;
            }

            _outbox.Complete(entry.RemoteMaintenanceId);
            _log.Info("queue.already-there",
                "Die Fernwartung stand bereits in TANSS — ein früherer Versuch ist "
                + "angekommen, seine Antwort ging verloren. Es wurde nichts gesendet.",
                entry.RemoteMaintenanceId, entry.Repository, entry.Payload.TicketId);

            return UploadOutcome.AlreadyThere;
        }
        catch (TanssException exception)
        {
            // NICHT senden. "Konnte nicht gefragt werden" ist nicht dasselbe wie "nicht
            // vorhanden" - und der Unterschied kostet im Zweifel eine Dublette, die jemand von
            // Hand aus der Datenbank schneiden muss.
            _outbox.Fail(entry.RemoteMaintenanceId,
                Redaction.Scrub(exception.Message), outcomeUnknown: true);

            _log.Warning("queue.postponed",
                "Zurückgestellt: Der Ausgang des letzten Versuchs ist ungeklärt, und die "
                + "Existenzprüfung liess sich nicht durchführen. Gesendet wurde nichts — TANSS "
                + "dedupliziert nicht. " + Redaction.Scrub(exception.Message),
                entry.RemoteMaintenanceId, entry.Repository, entry.Payload.TicketId);

            return UploadOutcome.Postponed;
        }
    }

    private void Fail(QueuedCommit entry, Exception exception, bool outcomeUnknown,
                      string eventName)
    {
        string message = Redaction.Scrub(exception.Message);

        _outbox.Fail(entry.RemoteMaintenanceId, message, outcomeUnknown);
        _log.Error(eventName, message, entry.RemoteMaintenanceId, entry.Repository,
                   entry.Payload.TicketId);
    }
}
