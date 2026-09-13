using System.Text.Json.Serialization;
using TanssGitConnector.Api;
using TanssGitConnector.Git;

namespace TanssGitConnector.Storage.Config;

/// <summary>
/// Die gesamte Konfiguration des Werkzeugs, so wie sie in <c>config.json</c> steht.
/// </summary>
/// <remarks>
/// <para><b>Unbekannte Felder werden abgelehnt</b> (<see cref="JsonUnmappedMemberHandling.Disallow"/>).
/// Eine vertippte Einstellung, die stillschweigend ignoriert wird, ist schlimmer als ein
/// Fehler: Wer <c>only_with_tickets</c> statt <c>only_with_ticket</c> schreibt, hält seine
/// Einschränkung für aktiv, während jeder Commit gebucht wird. Der Preis dafür ist, dass eine
/// ältere Programmfassung eine neuere Datei nicht liest — dafür gibt es <see cref="Version"/>.</para>
///
/// <para>Geheimnisse stehen hier <b>nicht</b> drin. Das Token liegt geschützt im
/// Zustandsverzeichnis, siehe <c>Secrets</c>.</para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppConfig
{
    /// <summary>Höchster Stand, den diese Programmfassung lesen kann.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Stand des Dateiaufbaus. Erlaubt später eine Überführung ohne Rätselraten.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Zugang zur TANSS-Instanz.</summary>
    public required TanssSection Tanss { get; init; }

    /// <summary>Wie aus einem Commit eine Fernwartung wird.</summary>
    public required CommitsSection Commits { get; init; }

    /// <summary>Woher die Ticketnummer kommt und ob sie geprüft wird.</summary>
    public TicketsSection Tickets { get; init; } = new();

    /// <summary>Das Verhalten des <c>post-commit</c>-Hakens.</summary>
    public HookSection Hook { get; init; } = new();

    /// <summary>Vorgeschalteter Proxy. Bleibt abgeschaltet, wenn keiner nötig ist.</summary>
    public ProxySection Proxy { get; init; } = new();

    /// <summary>Protokollierung auf dem Rechner des Technikers.</summary>
    public LoggingSection Logging { get; init; } = new();

    /// <summary>Übersetzt in die Netzoptionen der API-Schicht.</summary>
    /// <remarks>
    /// <para>Der Weg führt bewusst nur in diese Richtung. Die API-Schicht kennt keine Dateipfade
    /// und soll das auch nicht.</para>
    /// <para><b>Das Proxy-Kennwort muss von aussen kommen.</b> In der Konfiguration steht es
    /// nicht, und dieses Stück hat keinen Zugang zum Tokenspeicher.</para>
    /// </remarks>
    /// <param name="proxyPassword">Das aufgelöste Proxy-Kennwort, oder <see langword="null"/>.</param>
    public TanssOptions ToTanssOptions(string? proxyPassword = null) => new()
    {
        BaseUrl = Tanss.BaseUrl,
        EmployeeId = Tanss.EmployeeId,
        Timeout = TimeSpan.FromSeconds(Tanss.TimeoutSeconds),
        VerifyTls = Tanss.VerifyTls,
        Proxy = Proxy.Enabled
            ? new ProxyOptions
            {
                Address = Proxy.Address,
                Port = Proxy.Port,
                User = Proxy.User,
                Password = proxyPassword,
            }
            : null,
    };

    /// <summary>Übersetzt in die Einstellungen, aus denen eine Fernwartung entsteht.</summary>
    /// <remarks>
    /// Die Mitarbeiter-ID kommt aus dem Abschnitt <c>tanss</c> und nicht aus <c>commits</c>: Sie
    /// ist dieselbe, die als <c>loggedInUserId</c> an jeder Anfrage hängt. Zwei Stellen für
    /// denselben Wert wären zwei Stellen, an denen er auseinanderlaufen kann.
    /// </remarks>
    public BookingSettings ToBookingSettings() => new()
    {
        RemoteSupportTypeId = Commits.RemoteSupportTypeId,
        EmployeeId = Tanss.EmployeeId,
        Mode = Commits.DurationMode,
        Fixed = TimeSpan.FromMinutes(Commits.DurationMinutes),
        Minimum = TimeSpan.FromMinutes(Commits.MinimumMinutes),
        Maximum = TimeSpan.FromMinutes(Commits.MaximumMinutes),
        IncludeBody = Commits.IncludeBody,
        IncludeBranch = Commits.IncludeBranch,
        IncludeRepository = Commits.IncludeRepository,
    };

    /// <summary>Prüft die geladene Konfiguration und wirft bei Verstößen.</summary>
    /// <exception cref="ConfigValidationException">Mindestens eine Regel ist verletzt.</exception>
    public void Validate(string? origin = null) => ConfigValidator.Validate(this, origin);
}

/// <summary>Zugang zur TANSS-Instanz.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TanssSection
{
    /// <summary>
    /// Basisadresse <b>einschließlich</b> <c>/backend</c>, ohne Schrägstrich am Ende.
    /// </summary>
    /// <remarks>
    /// Zeigt die Adresse auf die Weboberfläche, antwortet die PHP-Oberfläche auf jede Anfrage
    /// mit HTTP 400 — auch ohne Token. Das ist der häufigste Einrichtungsfehler überhaupt und
    /// kostet ohne diesen Hinweis eine halbe Stunde.
    /// </remarks>
    public required string BaseUrl { get; init; }

    /// <summary>Die eigene Mitarbeiter-ID. Sie trägt die gesamte Attribution der Buchungen.</summary>
    public required int EmployeeId { get; init; }

    /// <summary>Ab welcher Restlaufzeit das Token erneuert wird.</summary>
    public int RotateBeforeDays { get; init; } = 60;

    /// <summary>Zertifikatsprüfung. <c>false</c> ist ein Notbehelf und wird im Doktor gemeldet.</summary>
    public bool VerifyTls { get; init; } = true;

    /// <summary>
    /// Zeitgrenze je Aufruf in Sekunden.
    /// </summary>
    /// <remarks>
    /// Knapper als bei einem Dienst, der im Hintergrund läuft: Am anderen Ende sitzt ein
    /// Techniker, dessen Commit gerade wartet.
    /// </remarks>
    public int TimeoutSeconds { get; init; } = 15;
}

/// <summary>Wie aus einem Commit eine Fernwartung wird.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CommitsSection
{
    /// <summary>
    /// Die externe Fernwartungs-Anbindung in TANSS, auf die gebucht wird.
    /// </summary>
    /// <remarks>
    /// Muss mindestens 1000 sein und in TANSS unter „Externe Fernwartungs-Anbindungen
    /// verwalten“ angelegt sein. Eine eigene Anbindung namens „Entwicklung“ oder „Git“ ist
    /// sinnvoll: Sie trennt die Commits in jeder Auswertung von echten Fernwartungen.
    /// </remarks>
    public required int RemoteSupportTypeId { get; init; }

    /// <summary>Woraus sich die Dauer ergibt.</summary>
    public DurationMode DurationMode { get; init; } = DurationMode.Fixed;

    /// <summary>Die feste Dauer in Minuten — und der Rückfall, wenn sich nichts bemessen lässt.</summary>
    public int DurationMinutes { get; init; } = 15;

    /// <summary>Untergrenze in Minuten im Modus <c>since_last_commit</c>.</summary>
    public int MinimumMinutes { get; init; } = 5;

    /// <summary>Obergrenze in Minuten im Modus <c>since_last_commit</c>.</summary>
    /// <remarks>
    /// Der wichtigste Wert dieses Abschnitts. Ohne ihn buchte der erste Commit nach dem
    /// Wochenende zweiundsiebzig Stunden.
    /// </remarks>
    public int MaximumMinutes { get; init; } = 120;

    /// <summary>Soll der Rumpf der Commit-Meldung mit nach TANSS?</summary>
    /// <remarks>
    /// Voreingestellt an: Der Rumpf ist die Begründung der Arbeit und damit genau das, was in
    /// der Dokumentation beim Kunden fehlt, wenn er fehlt. Wer Rümpfe schreibt, in denen
    /// Interna stehen, schaltet ihn ab.
    /// </remarks>
    public bool IncludeBody { get; init; } = true;

    /// <summary>Soll der Zweigname mit nach TANSS?</summary>
    public bool IncludeBranch { get; init; } = true;

    /// <summary>Soll der Name des Repositorys mit nach TANSS?</summary>
    public bool IncludeRepository { get; init; } = true;

    /// <summary>
    /// Zusammenführungs-Commits übergehen.
    /// </summary>
    /// <remarks>
    /// Voreingestellt an. Ein <c>merge</c> trägt selten eigene Arbeitszeit — die steckt in den
    /// Commits, die er zusammenführt, und die sind meist schon gebucht. Wer ihn trotzdem bucht,
    /// zahlt dieselbe Arbeit zweimal in die Auswertung ein.
    /// </remarks>
    public bool SkipMergeCommits { get; init; } = true;

    /// <summary>
    /// Nur Commits buchen, zu denen eine Ticketnummer vorliegt.
    /// </summary>
    /// <remarks>
    /// <b>Voreingestellt aus, und mit Bedacht.</b> Eingeschaltet ist das die schärfste
    /// Datenschutzeinstellung dieses Werkzeugs: Was nicht ausdrücklich einem Ticket zugeordnet
    /// ist — das eigene Werkzeug am Abend, das Übungsprojekt, das private Repository —
    /// verlässt den Rechner nicht. Ausgeschaltet ist es die vollständigere Dokumentation.
    /// Beides ist vertretbar, und deshalb entscheidet es der Betrieb und nicht dieses Werkzeug.
    /// </remarks>
    public bool OnlyWithTicket { get; init; }
}

/// <summary>Woher die Ticketnummer kommt und ob sie geprüft wird.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketsSection
{
    /// <summary>Ticketnummer aus einem Zweignamen lesen, der auf <c>#&lt;Nummer&gt;</c> endet.</summary>
    public bool FromBranch { get; init; } = true;

    /// <summary>Ticketnummer aus einer Zeile <c>Ticket: &lt;Nummer&gt;</c> der Commit-Meldung lesen.</summary>
    public bool FromMessage { get; init; } = true;

    /// <summary>
    /// Vor dem Buchen prüfen, ob es das Ticket gibt.
    /// </summary>
    /// <remarks>
    /// Kostet einen zusätzlichen Aufruf je Commit und verhindert den teuersten Fehler dieses
    /// Werkzeugs: eine Fernwartung an einer Nummer, die kein Ticket ist — sie taucht dann in
    /// keiner Auswertung auf. Eine misslungene Prüfung hält nichts auf.
    /// </remarks>
    public bool Verify { get; init; } = true;
}

/// <summary>Das Verhalten des <c>post-commit</c>-Hakens.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HookSection
{
    /// <summary>
    /// Nach dem Einreihen gleich senden?
    /// </summary>
    /// <remarks>
    /// Voreingestellt an. Aus ergibt einen Haken, der nur einreiht — dann bringt ein
    /// <c>tanss-git queue --flush</c> die Commits nach TANSS, etwa aus einem Zeitplan heraus.
    /// Das ist der richtige Weg auf einem Rechner ohne ständige Verbindung zur Instanz.
    /// </remarks>
    public bool SendImmediately { get; init; } = true;

    /// <summary>
    /// Wie lange der Haken beim Senden höchstens braucht, in Sekunden.
    /// </summary>
    /// <remarks>
    /// Das ist die Zeit, die der Techniker nach jedem Commit wartet. Läuft sie ab, bleibt der
    /// Commit in der Warteschlange und geht beim nächsten Mal mit — verloren ist nichts.
    /// </remarks>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>Nichts ausgeben, wenn alles gutgegangen ist.</summary>
    /// <remarks>
    /// Voreingestellt aus: Eine Zeile je Commit ist der Beleg, dass gebucht wurde. Wer sie
    /// abschaltet, merkt einen stillen Ausfall erst am Monatsende.
    /// </remarks>
    public bool Quiet { get; init; }
}

/// <summary>Vorgeschalteter Proxy.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProxySection
{
    public bool Enabled { get; init; }
    public string Address { get; init; } = string.Empty;
    public int Port { get; init; } = 8080;
    public string? User { get; init; }
}

/// <summary>Protokollierung auf dem Rechner des Technikers.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoggingSection
{
    /// <summary><c>debug</c>, <c>info</c>, <c>warning</c> oder <c>error</c>.</summary>
    public string Level { get; init; } = "info";

    /// <summary>
    /// Aufbewahrung des Änderungsprotokolls in Tagen.
    /// </summary>
    /// <remarks>
    /// Gilt für das Protokoll und für die <b>erledigten</b> Einträge der Warteschlange. Für
    /// wartende gilt sie nicht, gleich wie alt sie werden: Sie sind ungebuchte Arbeitszeit.
    /// </remarks>
    public int RetentionDays { get; init; } = 30;
}
