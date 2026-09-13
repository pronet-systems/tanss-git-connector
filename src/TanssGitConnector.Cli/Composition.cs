using TanssGitConnector.Api.Contract;
using TanssGitConnector.Api.Http;
using TanssGitConnector.Api.Repository;
using TanssGitConnector.Git;
using TanssGitConnector.Storage.Config;
using TanssGitConnector.Storage.Logging;
using TanssGitConnector.Storage.Queue;
using TanssGitConnector.Storage.Secrets;

namespace TanssGitConnector.Cli;

/// <summary>
/// Baut die Bausteine eines Laufs und gibt sie am Ende wieder frei.
/// </summary>
/// <remarks>
/// <para>Ein Aufruf dieses Werkzeugs ist kurz: Er baut, tut eine Sache und endet. Ein
/// Abhängigkeitsbehälter wäre dafür Aufwand ohne Ertrag — hier steht stattdessen an einer
/// Stelle, was womit zusammenhängt.</para>
/// <para><b>Das Token wird hier nicht gelesen.</b> Der Zugang fragt den Speicher bei jedem
/// Aufruf selbst. Deshalb lässt sich diese Zusammenstellung auch dann bauen, wenn noch gar kein
/// Token hinterlegt ist — der Einrichtungsassistent braucht genau das.</para>
/// </remarks>
internal sealed class Composition : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    /// <summary>Baut alles, was ein Lauf braucht.</summary>
    /// <param name="config">Die geladene Konfiguration.</param>
    /// <param name="tokens">Der Tokenspeicher; ohne Angabe der vorgesehene der Plattform.</param>
    public Composition(AppConfig config, ITokenStore? tokens = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        Config = config;
        Tokens = tokens ?? TokenProtection.Default();

        TanssClient client = new(config.ToTanssOptions(), Tokens);
        _owned.Add(client);

        Client = client;
        RemoteSupports = new RemoteSupportRepository(client, config.Tanss.EmployeeId);
        Technicians = new TechnicianRepository(client);
        Tickets = new TicketRepository(client);

        Outbox = CommitOutbox.Default();
        Log = ActivityLog.Default();
        Git = new GitRunner();
        Repository = new GitRepository(Git);
        Hooks = new HookInstaller(Git);
    }

    /// <summary>Die Konfiguration dieses Laufs.</summary>
    public AppConfig Config { get; }

    /// <summary>Der Tokenspeicher.</summary>
    public ITokenStore Tokens { get; }

    /// <summary>Der HTTP-Zugang zu TANSS.</summary>
    public ITanssClient Client { get; }

    /// <summary>Fernwartungen anlegen und nachschlagen.</summary>
    public IRemoteSupportRepository RemoteSupports { get; }

    /// <summary>Die Techniker der Instanz.</summary>
    public ITechnicianRepository Technicians { get; }

    /// <summary>Die Ticketprüfung.</summary>
    public ITicketVerification Tickets { get; }

    /// <summary>Die Warteschlange der Commits.</summary>
    public CommitOutbox Outbox { get; }

    /// <summary>Das Änderungsprotokoll.</summary>
    public ActivityLog Log { get; }

    /// <summary>Der Aufrufer für <c>git</c>.</summary>
    public IGitRunner Git { get; }

    /// <summary>Der lesende Zugriff auf ein Repository.</summary>
    public GitRepository Repository { get; }

    /// <summary>Das Einrichten des Hooks.</summary>
    public HookInstaller Hooks { get; }

    /// <summary>
    /// Baut einen zweiten Zugang mit einem vorgegebenen Token.
    /// </summary>
    /// <remarks>
    /// Für die Gegenprobe bei der Tokenerneuerung: Das frisch geprägte Token wird mit einem
    /// echten Aufruf geprüft, <b>bevor</b> es das bisherige ersetzt. Ohne diesen Weg müsste
    /// erst geschrieben und dann geprüft werden — und ein unbrauchbares neues Token liesse den
    /// Techniker ohne jeden Zugang zurück.
    /// </remarks>
    /// <param name="token">Das Token, mit oder ohne <c>Bearer </c>.</param>
    public ITanssClient CreateClientWith(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        TanssClient client = new(Config.ToTanssOptions(), new FixedToken(token));
        _owned.Add(client);
        return client;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (IDisposable item in _owned)
        {
            item.Dispose();
        }

        _owned.Clear();
    }

    /// <summary>Ein Tokenspeicher, der genau einen Wert kennt und nichts schreibt.</summary>
    /// <remarks>
    /// <see cref="Write"/> wirft mit Absicht statt stillschweigend nichts zu tun: Dieser
    /// Speicher gehört in die Gegenprobe, und ein Schreibversuch darauf wäre ein Denkfehler,
    /// der ohne Ausnahme unbemerkt bliebe.
    /// </remarks>
    private sealed class FixedToken : ITokenStore
    {
        private readonly string _token;

        public FixedToken(string token) => _token = TokenProtection.Normalize(token);

        public string Read() => _token;

        public void Write(string token) =>
            throw new InvalidOperationException(
                "Dieser Tokenspeicher dient der Gegenprobe eines frisch geprägten Tokens und "
                + "kann nichts ablegen. Geschrieben wird über den echten Speicher, und zwar erst "
                + "nach bestandener Probe.");
    }
}
