namespace TanssGitConnector.Api;

/// <summary>
/// Die Routen, die dieses Werkzeug benutzt — und die Regel, welches Präfix
/// <c>loggedInUserId</c> verlangt und welches ihn nicht braucht.
/// </summary>
/// <remarks>
/// <para><b>Der überwiegende Teil dieser Routen ist undokumentiert.</b> Die OpenAPI-Beschreibung
/// von TANSS 10.10.0 kennt weder <c>/api/tanss.x/v1</c> noch <c>PUT /api/v1/remoteSupports</c>
/// noch <c>/api/v1/jwts</c>. Was hier steht, stammt aus dem Studium der Schnittstelle und aus
/// Messungen gegen eine Produktivinstanz der Version 10.10.0 — nicht aus einer Spezifikation.</para>
///
/// <para>Daraus folgt eine Betriebsregel: bricht eine dieser Routen nach einem TANSS-Update weg,
/// meldet das Werkzeug das ausdrücklich, statt still zu scheitern. Ein Commit, der lautlos nicht
/// gebucht wird, fehlt am Monatsende in der Abrechnung, und niemand weiß, wo er hin ist.</para>
/// </remarks>
public static class TanssRoutes
{
    /// <summary>
    /// Die Integrationsschnittstelle von TANSS. <c>loggedInUserId</c> ist hier überflüssig.
    /// </summary>
    /// <remarks>
    /// Der Pfad heißt serverseitig so — das Präfix stammt von TANSS und nicht von diesem
    /// Werkzeug. Es ist die Route, über die TANSS Fernwartungen externer Anbindungen
    /// entgegennimmt.
    /// </remarks>
    public const string IntegrationPrefix = "/api/tanss.x/v1";

    /// <summary>Reguläre Schnittstelle. Hier ist <c>loggedInUserId</c> zwingend.</summary>
    public const string V1Prefix = "/api/v1";

    // --- Fernwartungen -------------------------------------------------------------------
    /// <summary>Anlegen. Einziges Verb auf dieser Route ist POST.</summary>
    public const string RemoteSupportsCreate = IntegrationPrefix + "/remoteSupports";

    /// <summary>Die in TANSS gepflegten externen Anbindungen.</summary>
    public const string RemoteSupportSystems = IntegrationPrefix + "/remoteSupports/systems";

    /// <summary>
    /// Filterabfrage über PUT. Undokumentiert, aber die einzige Leseroute für uns — und damit
    /// die Grundlage der Existenzprüfung, ohne die keine Wiederholung stattfindet.
    /// </summary>
    public const string RemoteSupportsList = V1Prefix + "/remoteSupports";

    // --- Tickets und Personen ------------------------------------------------------------
    /// <summary>
    /// Ein einzelnes Ticket. Antwortet mit 404, wenn es die Kennung nicht gibt — der Weg, eine
    /// aus einem Zweignamen gelesene Nummer zu prüfen, statt sie ungeprüft zu buchen.
    /// </summary>
    public static string TicketById(int ticketId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{V1Prefix}/tickets/{ticketId}");

    /// <summary>Die Techniker der Instanz. Dient der Einrichtung: hier steht die eigene ID.</summary>
    public const string Technicians = IntegrationPrefix + "/technicians";

    // --- Anmeldung und Token -------------------------------------------------------------
    public const string Login = V1Prefix + "/login";

    /// <summary>
    /// Alles, was TANSS ein Token ausstellen lässt.
    /// </summary>
    /// <remarks>
    /// Der Riegel gegen Wiederholungen hängt am <b>Präfix</b> und nicht an einer einzelnen
    /// Route: Ein Pfad, der ein unwiderrufliches Token ausstellt, darf nicht erst dann geschützt
    /// werden, wenn jemand daran denkt.
    /// </remarks>
    public const string JwtPrefix = V1Prefix + "/jwts";

    /// <summary>Token prägen. <c>tanss_app</c> ist der gültige Wert — <c>tanss_x</c> gibt es nicht.</summary>
    public const string MintToken = JwtPrefix + "/tanss_app";

    /// <summary>
    /// Braucht der Pfad den Parameter <c>loggedInUserId</c>?
    /// </summary>
    /// <remarks>
    /// Die Prüfung auf das <c>tanss.x</c>-Präfix steht <b>zuerst</b>: beide Präfixe beginnen
    /// zwar nicht gemeinsam, aber die Reihenfolge macht die Absicht lesbar und schützt vor
    /// einer späteren Umstellung auf einen gemeinsamen Stamm.
    /// </remarks>
    public static bool NeedsLoggedInUserId(string path) =>
        !path.StartsWith(IntegrationPrefix, StringComparison.Ordinal)
        && path.StartsWith(V1Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Hinterlässt ein <c>GET</c> auf diesen Pfad serverseitig eine Spur, die sich nicht
    /// zurücknehmen lässt?
    /// </summary>
    /// <remarks>
    /// <para><see cref="MintToken"/> ist trotz des Verbs <b>kein Lesevorgang</b>: TANSS stellt
    /// bei jedem Aufruf ein neues JWT aus. Ein zweiter Versuch nach einer Zeitüberschreitung
    /// liest also nicht dasselbe noch einmal, sondern prägt ein <b>zweites</b> Token. Behalten
    /// wird höchstens eines; die übrigen bleiben gültig, und TANSS 10.10.0 kennt keinen
    /// Widerruf. Solche Pfade laufen deshalb mit genau einem Versuch.</para>
    /// <para><b>Geprüft wird gegen <see cref="JwtPrefix"/> und nicht gegen die eine bekannte
    /// Route</b>, damit jede Tokenart, die dort später dazukommt, vom ersten Aufruf an
    /// geschützt ist.</para>
    /// </remarks>
    public static bool HasSideEffectOnGet(string path) =>
        path.StartsWith(JwtPrefix, StringComparison.Ordinal);
}
