using System.Text.Json.Serialization;

namespace TanssGitConnector.Api.Model;

/// <summary>Ein Ticket, reduziert auf das, was die Prüfung einer Nummer aus dem Zweignamen braucht.</summary>
public sealed record Ticket
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("companyId")] public int CompanyId { get; init; }
    [JsonPropertyName("statusId")] public int StatusId { get; init; }
    [JsonPropertyName("typeId")] public int TypeId { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"#{Id} {Title}";
}

/// <summary>Ein Techniker aus <c>/api/tanss.x/v1/technicians</c>.</summary>
public sealed record Technician
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("firstName")] public string? FirstName { get; init; }
    [JsonPropertyName("lastName")] public string? LastName { get; init; }
    [JsonPropertyName("emailAddress")] public string? EmailAddress { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Id} {Name}";
}

/// <summary>
/// Wie die Prüfung einer Ticketnummer ausgegangen ist.
/// </summary>
/// <remarks>
/// <para><b>Drei Antworten, nicht zwei.</b> Ein Rückgabewert <c>Ticket?</c> kennt nur „gefunden“
/// und „nicht gefunden“ — und muss deshalb einen Netzfehler zu einem der beiden schlagen. Beide
/// Ausgänge wären falsch: „gefunden“ verschweigt einen Zahlendreher, „nicht gefunden“ behauptet
/// über ein Ticket, das es sehr wohl gibt, das Gegenteil. <see cref="Undetermined"/> ist der
/// dritte Fall, und er ist der wichtige.</para>
/// <para><b>Keiner dieser Ausgänge hält das Buchen an.</b> Bei diesem Werkzeug stammt die
/// Nummer aus einem Zweignamen; ist sie falsch, ist der Commit trotzdem Arbeitszeit. Gebucht
/// wird dann ohne Ticketbezug, und der Grund steht im Protokoll — ein verworfener Commit wäre
/// verlorene Arbeitszeit, die niemand mehr findet.</para>
/// </remarks>
public enum TicketCheckOutcome
{
    /// <summary>
    /// Es wurde keine Ticketnummer gefunden; TANSS wurde gar nicht erst gefragt.
    /// </summary>
    /// <remarks>
    /// Kein Fehler, sondern der Normalfall bei einem Zweig ohne Ticketnummer. Ohne Ticketbezug
    /// zu buchen ist erlaubt — TANSS führt die 0 selbst als „kein Ticket“.
    /// </remarks>
    NoTicketNumber,

    /// <summary>Das Ticket gibt es. <see cref="TicketCheck.Summary"/> trägt Titel und Firma.</summary>
    Exists,

    /// <summary>
    /// Das Ticket gibt es <b>nachweislich</b> nicht.
    /// </summary>
    /// <remarks>
    /// Nachgemessen gegen eine Instanz der Fassung 10.10.0: <c>GET /api/v1/tickets/999999999</c>
    /// antwortet mit <b>404</b> und dem Rumpf <c>{"error":{"text":"OBJECT_NOT_FOUND", …}}</c>.
    /// Die Beschreibung der Schnittstelle kennt zu dieser Route nur 200 und 403; das 404 ist
    /// gemessen und real.
    /// </remarks>
    DoesNotExist,

    /// <summary>
    /// <b>Nicht ermittelt.</b> Es ließ sich nicht klären, ob es das Ticket gibt.
    /// </summary>
    /// <remarks>
    /// Netzfehler, Zeitüberschreitung, 5xx, eine 403 — und der 404, der nicht
    /// <c>OBJECT_NOT_FOUND</c> meldet.
    /// </remarks>
    Undetermined,
}

/// <summary>Das Ergebnis der Prüfung einer Ticketnummer.</summary>
public sealed record TicketCheck
{
    /// <summary>Wie die Antwort zu lesen ist.</summary>
    public TicketCheckOutcome Outcome { get; init; }

    /// <summary>Die geprüfte Nummer, so wie sie hereinkam.</summary>
    public int TicketId { get; init; }

    /// <summary>
    /// Das Ticket — nur bei <see cref="TicketCheckOutcome.Exists"/> gesetzt.
    /// </summary>
    /// <remarks>
    /// Bei <see cref="TicketCheckOutcome.Undetermined"/> bewusst <see langword="null"/>: Ein
    /// halbes Ticket aus einer misslungenen Prüfung anzuzeigen wäre eine vorgetäuschte Prüfung.
    /// </remarks>
    public Ticket? Ticket { get; init; }

    /// <summary>Der Firmenname zum Ticket, oder <see langword="null"/>, wenn keiner vorliegt.</summary>
    public string? CompanyName { get; init; }

    /// <summary>Der HTTP-Status, an dem die Prüfung hing; <see langword="null"/>, wenn keiner anfiel.</summary>
    public int? Status { get; init; }

    /// <summary>Die technische Meldung zum Fehlschlag, bereits geschwärzt; sonst <see langword="null"/>.</summary>
    public string? Failure { get; init; }

    /// <summary>Ein fertiger deutscher Satz für Anzeige und Protokoll.</summary>
    /// <remarks>
    /// Fertig formuliert und nicht bloß ein Schlüsselwort, damit niemand anderswo aus
    /// <see cref="TicketCheckOutcome.Undetermined"/> doch wieder „Ticket gibt es nicht“ macht.
    /// </remarks>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>
    /// Darf der Ticketbezug gesetzt werden, soweit überhaupt eine Nummer vorliegt?
    /// </summary>
    /// <remarks>
    /// <b>Nur ein belegtes <see cref="TicketCheckOutcome.DoesNotExist"/> hält den Ticketbezug
    /// auf.</b> Bei <see cref="TicketCheckOutcome.Undetermined"/> ausdrücklich nicht: Eine
    /// misslungene Prüfung ist kein Beleg gegen das Ticket, und ein weggelassener Ticketbezug
    /// wäre eine Fernwartung, die beim Kunden niemand zuordnen kann. Bei
    /// <see cref="TicketCheckOutcome.NoTicketNumber"/> gibt es nichts zu verhindern — dort ist
    /// die Nummer ohnehin 0.
    /// </remarks>
    public bool MayLink => Outcome != TicketCheckOutcome.DoesNotExist;

    /// <summary>
    /// Die Zeile, die den Zahlendreher auffallen lässt: „#5000 Serverstörung — Müller GmbH“.
    /// </summary>
    /// <remarks>
    /// Leer, solange kein Ticket vorliegt. Die Nummer stammt aus <see cref="TicketId"/> und
    /// nicht aus dem Rumpf — so steht dort auch dann die geprüfte Nummer, wenn TANSS das Feld
    /// <c>id</c> einmal nicht mitschickt.
    /// </remarks>
    public string Summary => Ticket is null
        ? string.Empty
        : CompanyName is { Length: > 0 } company
            ? $"#{TicketId} {Ticket.Title} — {company}"
            : $"#{TicketId} {Ticket.Title}";
}
