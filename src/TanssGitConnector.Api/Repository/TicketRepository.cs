using TanssGitConnector.Api.Contract;
using TanssGitConnector.Api.Diagnostics;
using TanssGitConnector.Api.Http;
using TanssGitConnector.Api.Model;

namespace TanssGitConnector.Api.Repository;

/// <summary>
/// Prüft eine Ticketnummer gegen TANSS.
/// </summary>
/// <remarks>
/// <para>Die Nummer stammt bei diesem Werkzeug aus dem Namen des Zweigs, auf den committet
/// wurde — also aus einem Text, den ein Mensch getippt hat. Ein Zahlendreher ist damit kein
/// Randfall, sondern zu erwarten, und eine Fernwartung mit einer Nummer ohne Ticket taucht in
/// keiner Auswertung auf.</para>
/// <para><b>Die Prüfung hält den Commit trotzdem nie auf.</b> Sie entscheidet allein darüber,
/// ob der Ticketbezug gesetzt wird; gebucht wird in jedem Fall. Ein verworfener Commit wäre
/// verlorene Arbeitszeit, die niemand mehr findet.</para>
/// </remarks>
public sealed class TicketRepository : ITicketVerification
{
    private readonly ITanssClient _client;
    private readonly ITanssMetaRead? _meta;

    /// <summary>Baut das Repository.</summary>
    /// <remarks>
    /// Erfüllt der Zugang zusätzlich <see cref="ITanssMetaRead"/>, liest die Prüfung den
    /// <c>meta</c>-Block mit und kann den Firmennamen nennen. Erfüllt er ihn nicht, läuft alles
    /// wie sonst, nur ohne Namen — das kostet Bequemlichkeit, nicht die Prüfung.
    /// </remarks>
    /// <param name="client">Der HTTP-Zugang.</param>
    public TicketRepository(ITanssClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _meta = client as ITanssMetaRead;
    }

    /// <inheritdoc />
    public async Task<TicketCheck> CheckAsync(int ticketId, CancellationToken ct = default)
    {
        if (ticketId <= 0)
        {
            // TANSS fuehrt die 0 selbst als "kein Ticket". Danach zu fragen hiesse, auf eine
            // Frage zu antworten, die niemand gestellt hat.
            return new TicketCheck
            {
                Outcome = TicketCheckOutcome.NoTicketNumber,
                TicketId = ticketId,
                Explanation = "Der Zweigname nennt keine Ticketnummer. Gebucht wird ohne "
                    + "Ticketbezug.",
            };
        }

        try
        {
            (Ticket? ticket, LinkedEntities names) =
                await ReadAsync(ticketId, ct).ConfigureAwait(false);

            if (ticket is null)
            {
                // 200 mit leerem Rumpf oder null-Inhalt: weder ein Ticket noch eine Absage.
                return Undetermined(ticketId,
                    "TANSS hat mit Erfolg geantwortet, aber kein Ticket genannt.", 200);
            }

            if (ticket.Id != 0 && ticket.Id != ticketId)
            {
                // NICHT GEMESSEN, nur abgefangen: dass TANSS ein anderes Ticket herausgibt als
                // das angefragte. Faende es statt, waere das angezeigte Ticket eine Luege.
                return Undetermined(ticketId,
                    $"TANSS hat auf die Anfrage nach Ticket {ticketId} das Ticket {ticket.Id} "
                    + "geliefert. Welches gemeint ist, ist damit offen.", 200);
            }

            // Ohne Firma am Ticket bleibt der Name leer, statt "Firma 0" zu behaupten.
            string? company = ticket.CompanyId > 0 ? names.CompanyName(ticket.CompanyId) : null;

            return new TicketCheck
            {
                Outcome = TicketCheckOutcome.Exists,
                TicketId = ticketId,
                Ticket = ticket,
                CompanyName = company,
                Status = 200,
                Explanation = $"Ticket {ticketId} gibt es. Ein Zahlendreher trifft oft ein "
                    + "echtes Ticket beim falschen Kunden — Titel und Firma stehen deshalb im "
                    + "Protokoll.",
            };
        }
        catch (TanssNotFoundException ex)
        {
            // GEMESSEN: 404 mit error.text = OBJECT_NOT_FOUND heisst "gibt es nicht". Nur dieser
            // Beleg darf den Ticketbezug verhindern. Ein 404 ohne diese Marke kann ebenso gut
            // eine Route sein, die es in dieser TANSS-Version nicht mehr gibt - die hier
            // benutzten Routen sind ueberwiegend undokumentiert.
            return Mentions(ex.Detail, "NOT_FOUND")
                ? new TicketCheck
                {
                    Outcome = TicketCheckOutcome.DoesNotExist,
                    TicketId = ticketId,
                    Status = ex.Status,
                    Failure = Redaction.Scrub(ex.Message),
                    Explanation = $"Ticket {ticketId} gibt es in TANSS nicht — TANSS meldet "
                        + "OBJECT_NOT_FOUND. Die Fernwartung wird ohne Ticketbezug gebucht; die "
                        + "Nummer im Zweignamen ist zu berichtigen.",
                }
                : Undetermined(ticketId,
                    $"TANSS hat die Anfrage nach Ticket {ticketId} mit 404 beantwortet, aber "
                    + "ohne OBJECT_NOT_FOUND. Das kann heißen, dass es das Ticket nicht gibt — "
                    + "oder dass es die Route in dieser TANSS-Version nicht mehr gibt.",
                    ex.Status, ex.Message);
        }
        catch (TanssException ex)
        {
            return Undetermined(ticketId,
                $"Ob es Ticket {ticketId} gibt, liess sich nicht klären. Gebucht wird mit "
                + "Ticketbezug — eine misslungene Prüfung ist kein Beleg gegen das Ticket.",
                ex.Status, ex.Message);
        }
    }

    /// <summary>Liest ein Ticket samt <c>meta</c>-Block, sofern der Zugang das hergibt.</summary>
    private async Task<(Ticket? Ticket, LinkedEntities Names)> ReadAsync(int ticketId,
                                                                         CancellationToken ct)
    {
        string route = TanssRoutes.TicketById(ticketId);

        if (_meta is null)
        {
            return (await _client.GetAsync<Ticket>(route, ct: ct).ConfigureAwait(false),
                    LinkedEntities.Empty);
        }

        (Ticket? ticket, IReadOnlyDictionary<string, object?> meta) =
            await _meta.GetWithMetaAsync<Ticket>(route, ct: ct).ConfigureAwait(false);

        return (ticket, LinkedEntities.From(meta));
    }

    private static TicketCheck Undetermined(int ticketId, string explanation, int? status,
                                            string? failure = null) => new()
    {
        Outcome = TicketCheckOutcome.Undetermined,
        TicketId = ticketId,
        Status = status,
        Failure = failure is null ? null : Redaction.Scrub(failure),
        Explanation = explanation,
    };

    private static bool Mentions(string? detail, string needle) =>
        detail is not null && detail.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
