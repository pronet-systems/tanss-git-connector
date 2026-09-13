using TanssGitConnector.Api.Model;

namespace TanssGitConnector.Api.Contract;

/// <summary>
/// Woher das Arbeitstoken kommt und wohin das erneuerte geht.
/// </summary>
/// <remarks>
/// Bewusst eine Schnittstelle: die Netzschicht soll nichts über DPAPI, Dateirechte oder
/// Betriebssysteme wissen. Die Umsetzungen liegen in <c>TanssGitConnector.Storage</c> — eine je
/// Plattform, weil der Schutz des Geheimnisses auf Windows und auf Unix verschieden aussieht.
/// </remarks>
public interface ITokenStore
{
    /// <summary>Liefert das Token einschließlich des Präfixes <c>Bearer </c>.</summary>
    string Read();

    /// <summary>
    /// Ersetzt das Token. Die Umsetzung legt vorher eine Sicherung des bisherigen an — ein
    /// misslungener Wechsel darf nicht bedeuten, dass gar kein Token mehr da ist.
    /// </summary>
    void Write(string token);
}

/// <summary>Reiner HTTP-Zugriff auf TANSS. Kennt keine Fachlogik.</summary>
/// <remarks>
/// <para><b>Zwei Präfixe, eine Pflicht.</b> Auf <c>/api/v1/**</c> ist <c>loggedInUserId</c>
/// zwingend — ohne ihn antwortet TANSS mit 403. Auf <c>/api/tanss.x/v1/**</c> wird er
/// ignoriert. Die Umsetzung setzt ihn selbsttätig nur dort, wo er nötig ist; ein Aufrufer setzt
/// ihn nie von Hand.</para>
/// </remarks>
public interface ITanssClient : IDisposable
{
    /// <summary>
    /// Liest. Wird bei Netzfehlern und 5xx wiederholt — <b>außer</b> auf den Pfaden, die
    /// <see cref="TanssRoutes.HasSideEffectOnGet"/> als seiteneffektbehaftet kennt; die laufen
    /// mit genau einem Versuch, weil dort jeder Versuch serverseitig etwas anlegt.
    /// </summary>
    Task<T?> GetAsync<T>(string path, IDictionary<string, string?>? query = null,
                         CancellationToken ct = default);

    Task<T?> PutAsync<T>(string path, object? body = null,
                         IDictionary<string, string?>? query = null,
                         CancellationToken ct = default);

    Task<T?> PostAsync<T>(string path, object? body = null,
                          IDictionary<string, string?>? query = null,
                          CancellationToken ct = default);

    Task DeleteAsync(string path, IDictionary<string, string?>? query = null,
                     CancellationToken ct = default);

    /// <summary>
    /// Wie <see cref="PostAsync{T}"/>, liefert zusätzlich den <c>meta</c>-Block. Der trägt die
    /// <c>linkedEntities</c> und ist beim Anlegen einer Fernwartung der Nachweis, dass die
    /// Attribution serverseitig gegriffen hat.
    /// </summary>
    Task<(T? Content, IReadOnlyDictionary<string, object?> Meta)> PostWithMetaAsync<T>(
        string path, object? body = null, IDictionary<string, string?>? query = null,
        CancellationToken ct = default);
}

/// <summary>
/// Lesen <b>mit</b> dem <c>meta</c>-Block.
/// </summary>
/// <remarks>
/// <para><b>Warum es diesen Weg braucht.</b> <see cref="ITanssClient.GetAsync{T}"/> gibt nur den
/// <c>content</c> heraus und wirft den Umschlag weg. Der Name zu einer Kennung steht aber
/// ausschließlich im Umschlag: <c>GET /api/v1/tickets/{id}</c> trägt die <c>companyId</c> im
/// Inhalt und den Firmennamen nur unter <c>meta.linkedEntities.companies</c>. Ohne diesen Weg
/// bliebe zu einem geprüften Ticket nur eine Zahl.</para>
/// <para><b>Warum eine eigene Schnittstelle.</b> Der Grundvertrag bleibt schmal, damit ihn eine
/// Attrappe in wenigen Zeilen erfüllt. Wer den Umschlag braucht, prüft mit
/// <c>is ITanssMetaRead</c> darauf; wer nicht, merkt nichts davon.</para>
/// </remarks>
public interface ITanssMetaRead
{
    /// <summary>Liest wie <see cref="ITanssClient.GetAsync{T}"/> und gibt zusätzlich <c>meta</c> heraus.</summary>
    Task<(T? Content, IReadOnlyDictionary<string, object?> Meta)> GetWithMetaAsync<T>(
        string path, IDictionary<string, string?>? query = null, CancellationToken ct = default);

    /// <summary>Schickt wie <see cref="ITanssClient.PutAsync{T}"/> und gibt zusätzlich <c>meta</c> heraus.</summary>
    Task<(T? Content, IReadOnlyDictionary<string, object?> Meta)> PutWithMetaAsync<T>(
        string path, object? body = null, IDictionary<string, string?>? query = null,
        CancellationToken ct = default);
}

/// <summary>
/// Fernwartungen anlegen und nachschlagen.
/// </summary>
/// <remarks>
/// <b>Die Existenzprüfung ist Teil des Vertrags und kein Zusatz.</b> TANSS dedupliziert nicht;
/// ohne <see cref="ExistsAsync(string, DateTimeOffset, DateTimeOffset, CancellationToken)"/>
/// dürfte nach einer Zeitüberschreitung überhaupt nicht wiederholt werden.
/// </remarks>
public interface IRemoteSupportRepository
{
    /// <summary>Legt eine Fernwartung an. Wird <b>niemals</b> automatisch wiederholt.</summary>
    Task<RemoteSupportRead> CreateAsync(RemoteSupportWrite item, CancellationToken ct = default);

    /// <summary>Legt an und meldet zusätzlich, ob die Attribution serverseitig gegriffen hat.</summary>
    Task<RemoteSupportCreateResult> CreateWithDiagnosticsAsync(RemoteSupportWrite item,
                                                               CancellationToken ct = default);

    /// <summary>Steht diese Vorgangskennung schon in TANSS?</summary>
    Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset around,
                           CancellationToken ct = default);

    /// <summary>Steht diese Vorgangskennung im Zeitfenster des Vorgangs schon in TANSS?</summary>
    Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset startedAt,
                           DateTimeOffset endedAt, CancellationToken ct = default);

    /// <summary>Steht diese Fernwartung schon in TANSS? Nimmt das Zeitfenster aus dem Datensatz.</summary>
    Task<bool> ExistsAsync(RemoteSupportWrite item, CancellationToken ct = default);

    /// <summary>Die Filterabfrage über <c>PUT /api/v1/remoteSupports</c>.</summary>
    Task<IReadOnlyList<RemoteSupportRead>> ListAsync(Timeframe timeframe, string? text = null,
                                                     CancellationToken ct = default);

    /// <summary>Die in TANSS gepflegten externen Fernwartungs-Anbindungen.</summary>
    Task<IReadOnlyList<RemoteSupportSystem>> ListSystemsAsync(CancellationToken ct = default);
}

/// <summary>Die Techniker der Instanz. Dient der Einrichtung und der Gegenprobe im Doktor.</summary>
public interface ITechnicianRepository
{
    /// <summary>Alle Techniker, die die Instanz kennt.</summary>
    Task<IReadOnlyList<Technician>> ListAsync(CancellationToken ct = default);
}

/// <summary>
/// Eine aus dem Zweignamen gelesene Ticketnummer prüfen — mit allen drei möglichen Antworten.
/// </summary>
/// <remarks>
/// <b>Diese Prüfung wirft nicht.</b> Jeder Fehlschlag wird zu
/// <see cref="TicketCheckOutcome.Undetermined"/> — ein Fehler kostet die Prüfung, nie den
/// Vorgang. Einzige Ausnahme ist der Abbruch durch den Aufrufer selbst; der geht als
/// <see cref="OperationCanceledException"/> durch, weil ein verschluckter Abbruch schlimmer ist
/// als ein durchgereichter.
/// </remarks>
public interface ITicketVerification
{
    /// <summary>
    /// Fragt TANSS, ob es dieses Ticket gibt, und holt Titel und Firma gleich mit.
    /// </summary>
    /// <param name="ticketId">Die zu prüfende Nummer; 0 und kleiner heißt „keine Nummer“.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Das Ergebnis; niemals <see langword="null"/>.</returns>
    Task<TicketCheck> CheckAsync(int ticketId, CancellationToken ct = default);
}
