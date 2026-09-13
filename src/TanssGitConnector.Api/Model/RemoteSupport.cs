using System.Text.Json.Serialization;

namespace TanssGitConnector.Api.Model;

/// <summary>
/// Eine Fernwartung, so wie TANSS sie entgegennimmt.
/// </summary>
/// <remarks>
/// Die Feldnamen sind die der Schnittstelle und damit bindend.
/// Nicht enthalten und bewusst nicht sendbar:
/// <list type="bullet">
///   <item><c>id</c> — könnte serverseitig auf eine bestehende Zeile binden.</item>
///   <item><c>fee</c> — TeamViewer-Altlast.</item>
///   <item><c>typeName</c> — reines Anzeigefeld des Servers.</item>
/// </list>
/// <para><c>linkTypeId</c> und <c>linkId</c> sind <b>nicht</b> abgebildet, und das ist Absicht:
/// Sie sind der <b>Geräteplatz</b>, und die Zahl, die dort für „PC“ steht, ist nicht gemessen.
/// Eine geratene Zahl verknüpfte die Fernwartung mit einem beliebigen anderen Datensatz — der
/// Ticketbezug läuft über <see cref="TicketId"/>.</para>
/// </remarks>
public sealed record RemoteSupportWrite
{
    /// <summary>Kennung der externen Anbindung, muss mindestens 1000 sein und in TANSS existieren.</summary>
    [JsonPropertyName("typeId")]
    public required int TypeId { get; init; }

    /// <summary>TANSS-Mitarbeiter-ID. Ein Wert ungleich 0 gewinnt immer und trägt die gesamte Attribution.</summary>
    [JsonPropertyName("employeeId")]
    public required int EmployeeId { get; init; }

    /// <summary>Unix-Sekunden. Siehe <see cref="TanssTime"/>.</summary>
    [JsonPropertyName("startTime")]
    public required long StartTime { get; init; }

    /// <summary>Unix-Sekunden. 0 speichert SQL NULL und bedeutet: läuft noch.</summary>
    /// <remarks>
    /// Ein Commit ist ein Zeitpunkt und kein laufender Vorgang; dieses Werkzeug sendet hier
    /// deshalb immer einen echten Wert. Eine Fernwartung ohne Ende bliebe in TANSS als
    /// laufende Sitzung stehen, die niemand schliesst.
    /// </remarks>
    [JsonPropertyName("endTime")]
    public required long EndTime { get; init; }

    /// <summary>
    /// Unsere Vorgangskennung — bei diesem Werkzeug der Commit-Hash.
    /// </summary>
    /// <remarks>
    /// <b>Sie ist die ganze Wiederholungssicherheit.</b> TANSS dedupliziert nicht; vor jeder
    /// Wiederholung steht deshalb die Existenzprüfung über genau dieses Feld. Ein Commit-Hash
    /// eignet sich dafür besser als jede erzeugte Kennung: Er ist eindeutig, er überlebt jeden
    /// Neustart, und er ist aus dem Repository jederzeit nachvollziehbar.
    /// </remarks>
    [JsonPropertyName("remoteMaintenanceId")]
    public required string RemoteMaintenanceId { get; init; }

    [JsonPropertyName("comment")]
    public string Comment { get; init; } = string.Empty;

    /// <summary>Undokumentiert, aber wirksam: erzeugt einen echten Ticketbezug. 0 = keiner.</summary>
    [JsonPropertyName("ticketId")]
    public int TicketId { get; init; }

    /// <summary>
    /// Benutzerkennung der Gegenstelle. Dieses Werkzeug setzt sie nicht.
    /// </summary>
    /// <remarks>
    /// <b>Geht als leere Zeichenkette hinaus, und das ist nachgemessen unbedenklich.</b> Anders
    /// als <see cref="DeviceId"/>, die TANSS in eine Firma zu übersetzen versucht, wird dieses
    /// Feld nicht aufgelöst: Am 13.09.2026 gegen eine Instanz der Fassung 10.10.0 angelegt
    /// (Fernwartung 38625) und zurückgelesen — <c>userId</c> und <c>userName</c> kamen leer
    /// zurück, ohne Nebenwirkung auf Zuordnung oder Firma.
    /// </remarks>
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    /// <summary>Anzeigename der Gegenstelle. Siehe <see cref="UserId"/>.</summary>
    [JsonPropertyName("userName")]
    public string UserName { get; init; } = string.Empty;

    /// <summary>
    /// Die Kennung, unter der TANSS ein Gerät wiedererkennt — oder <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Dieses Werkzeug setzt sie nicht.</b> Ein Commit gehört zu einem Repository und
    /// zu keinem Gerät des Kunden; eine erfundene Gerätekennung liesse TANSS die Fernwartung
    /// einer Firma zuordnen, die mit dem Commit nichts zu tun hat.</para>
    /// <para><b>Ausdrücklich <see langword="null"/>-fähig, und ausdrücklich ausgelassen, wenn
    /// sie fehlt.</b> Der Serialisierer dieses Hauses schreibt sonst jedes Feld mit
    /// (<c>DefaultIgnoreCondition.Never</c>), und ein leerer Wert ist keine Kennung: ihn zu
    /// senden hiesse, eine Zuordnung auf die leere Zeichenkette zu ermöglichen.</para>
    /// </remarks>
    [JsonPropertyName("deviceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceId { get; init; }

    /// <summary>
    /// Der Anzeigename des Geräts. Hier steht der Name des Repositorys.
    /// </summary>
    /// <remarks>
    /// Ein freier Anzeigetext, den TANSS nicht auflöst — anders als <see cref="DeviceId"/>, aus
    /// der TANSS eine Firma zu machen versucht. Er ist damit der einzige gefahrlose Ort für die
    /// Herkunft des Commits.
    /// </remarks>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>
    /// Die Firma, auf die gebucht wird — oder 0, und dann geht das Feld gar nicht hinaus.
    /// </summary>
    /// <remarks>
    /// <para><b>Dieses Werkzeug setzt es nicht.</b> An der Firma hängt in TANSS der Weg zur
    /// Leistung und damit zur Rechnung; in einen fakturierenden Vorgang greift dieses Werkzeug
    /// nicht hinein. Der Kundenbezug entsteht über das Ticket.</para>
    /// <para><b><c>WhenWritingDefault</c> und nicht <c>Never</c>:</b> Der Serialisierer dieses
    /// Hauses schriebe sonst bei <i>jeder</i> Fernwartung ein <c>"companyId": 0</c> mit, und
    /// eine gesendete 0 ist etwas anderes als ein fehlendes Feld — sie könnte eine vorhandene
    /// serverseitige Zuordnung überschreiben.</para>
    /// </remarks>
    [JsonPropertyName("companyId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CompanyId { get; init; }
}

/// <summary>Eine Fernwartung, wie TANSS sie zurückgibt.</summary>
public sealed record RemoteSupportRead
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("remoteMaintenanceId")] public string RemoteMaintenanceId { get; init; } = string.Empty;
    [JsonPropertyName("typeId")] public int TypeId { get; init; }
    [JsonPropertyName("typeName")] public string? TypeName { get; init; }
    [JsonPropertyName("employeeId")] public int EmployeeId { get; init; }
    [JsonPropertyName("userId")] public string? UserId { get; init; }
    [JsonPropertyName("userName")] public string? UserName { get; init; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; init; }
    [JsonPropertyName("deviceName")] public string? DeviceName { get; init; }
    [JsonPropertyName("companyId")] public int CompanyId { get; init; }
    [JsonPropertyName("linkTypeId")] public int LinkTypeId { get; init; }
    [JsonPropertyName("linkId")] public int LinkId { get; init; }
    [JsonPropertyName("startTime")] public long StartTime { get; init; }
    [JsonPropertyName("endTime")] public long EndTime { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    [JsonPropertyName("ticketId")] public int TicketId { get; init; }
}

/// <summary>
/// Eine externe Fernwartungsanbindung, wie sie in TANSS unter
/// „Externe Fernwartungs-Anbindungen verwalten“ gepflegt wird.
/// </summary>
public sealed record RemoteSupportSystem
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    /// <summary>Hintergrundfarbe als sechsstelliger Hexwert ohne Raute, etwa <c>00bfff</c>.</summary>
    [JsonPropertyName("backgroundColor")] public string? BackgroundColor { get; init; }

    /// <summary>Leistungstyp, mit dem TANSS diese Fernwartung in eine Leistung wandelt.</summary>
    [JsonPropertyName("supportTypeId")] public int SupportTypeId { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Id} {Name}";
}

/// <summary>
/// Das Ergebnis eines Anlegevorgangs samt Befund zur Attribution.
/// </summary>
/// <param name="Support">Der angelegte Datensatz, so wie TANSS ihn zurückgibt.</param>
/// <param name="AttributionConfirmed">
/// Hat TANSS den Mitarbeiter in <c>meta.linkedEntities.employees</c> ausgewiesen?
/// </param>
/// <param name="Warning">
/// Der Hinweistext, falls die Attribution nicht bestätigt ist — sonst <c>null</c>. Er ist zum
/// Protokollieren und Anzeigen gedacht, nicht zum Wiederholen des Aufrufs.
/// </param>
public sealed record RemoteSupportCreateResult(RemoteSupportRead Support, bool AttributionConfirmed,
                                               string? Warning);

/// <summary>Zeitfenster für Filterabfragen. Grenzen in Unix-Sekunden.</summary>
public sealed record Timeframe
{
    [JsonPropertyName("from")] public required long From { get; init; }
    [JsonPropertyName("till")] public required long Till { get; init; }
}
