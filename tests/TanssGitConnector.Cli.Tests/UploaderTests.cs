using TanssGitConnector.Api;
using TanssGitConnector.Api.Contract;
using TanssGitConnector.Api.Model;
using TanssGitConnector.Cli.Booking;
using TanssGitConnector.Storage.Logging;
using TanssGitConnector.Storage.Queue;
using Xunit;

namespace TanssGitConnector.Cli.Tests;

/// <summary>
/// Der Versand — und die Regel, deren Bruch am teuersten ist.
/// </summary>
/// <remarks>
/// TANSS dedupliziert nicht: Ein zweiter Aufruf mit derselben Kennung erzeugt einen zweiten
/// Datensatz, und der ist nur über einen direkten Datenbankzugriff wieder zu entfernen. Jeder
/// Test hier prüft genau einen Zweig dieser Regel.
/// </remarks>
public class UploaderTests
{
    private const string Sha = "3f2a1bc9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f4a3";

    [Fact]
    public async Task Ein_gewoehnlicher_Eintrag_wird_gesendet_und_ist_danach_erledigt()
    {
        using Harness harness = new();
        harness.Enqueue();

        FlushReport report = await harness.Uploader.FlushAsync();

        Assert.Equal(1, report.Sent);
        Assert.Equal(1, harness.Repository.Created);
        Assert.Equal(QueueState.Done, harness.Outbox.All()[0].State);
        Assert.Equal(38584, harness.Outbox.All()[0].RemoteSupportId);
    }

    [Fact]
    public async Task Eine_Ablehnung_laesst_den_Eintrag_liegen()
    {
        using Harness harness = new();
        harness.Enqueue();
        harness.Repository.FailWith = new TanssException("Abgewiesen.", 400);

        FlushReport report = await harness.Uploader.FlushAsync();

        Assert.Equal(1, report.Failed);

        QueuedCommit entry = harness.Outbox.All()[0];
        Assert.Equal(QueueState.Pending, entry.State);
        Assert.Equal(1, entry.Attempts);

        // TANSS hat geantwortet: Der Ausgang ist geklaert, es wurde nichts angelegt.
        Assert.False(entry.OutcomeUnknown);
    }

    /// <summary>
    /// Eine Zeitüberschreitung macht den Ausgang ungeklärt.
    /// </summary>
    /// <remarks>
    /// Die Anfrage kann angekommen, verarbeitet und nur die Antwort verloren gegangen sein.
    /// </remarks>
    [Fact]
    public async Task Eine_Zeitueberschreitung_macht_den_Ausgang_ungeklaert()
    {
        using Harness harness = new();
        harness.Enqueue();
        harness.Repository.FailWith = new TanssUnreachableException("Keine Antwort.");

        _ = await harness.Uploader.FlushAsync();

        Assert.True(harness.Outbox.All()[0].OutcomeUnknown);
    }

    /// <summary>
    /// Eine 5xx klärt den Ausgang <b>nicht</b>.
    /// </summary>
    /// <remarks>
    /// Der Server ist auf halbem Weg gestolpert. Ob er die Fernwartung vorher geschrieben hat,
    /// weiß von hier aus niemand — und genau deshalb darf sie nicht ohne Existenzprüfung
    /// wiederholt werden. Vorher galt jede Antwort ausser der Unerreichbarkeit als „geklärt:
    /// nichts angelegt“.
    /// </remarks>
    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Eine_Serverstoerung_laesst_den_Ausgang_offen(int status)
    {
        using Harness harness = new();
        harness.Enqueue();
        harness.Repository.FailWith = new TanssException("Serverfehler.", status);

        _ = await harness.Uploader.FlushAsync();

        Assert.True(harness.Outbox.All()[0].OutcomeUnknown);
    }

    /// <summary>
    /// Ein Fehler <b>nach</b> einer erfolgreichen Antwort lässt den Ausgang erst recht offen.
    /// </summary>
    /// <remarks>
    /// „TANSS hat quittiert, aber keinen Datensatz genannt“ und „der Rumpf ist kein JSON“ tragen
    /// keinen Status — sie entstehen erst, nachdem die Anfrage mit 2xx beantwortet wurde. Das
    /// ist der stärkste Hinweis darauf, dass der Datensatz sehr wohl angelegt wurde.
    /// </remarks>
    [Fact]
    public async Task Ein_Fehler_nach_erfolgreicher_Antwort_laesst_den_Ausgang_offen()
    {
        using Harness harness = new();
        harness.Enqueue();
        harness.Repository.FailWith = new TanssException(
            "TANSS hat das Anlegen quittiert, aber keinen Datensatz zurückgegeben.");

        _ = await harness.Uploader.FlushAsync();

        Assert.True(harness.Outbox.All()[0].OutcomeUnknown);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Eine_geprüfte_Ablehnung_klaert_den_Ausgang(int status)
    {
        using Harness harness = new();
        harness.Enqueue();
        harness.Repository.FailWith = new TanssException("Abgewiesen.", status);

        _ = await harness.Uploader.FlushAsync();

        // TANSS hat die Anfrage angesehen und Nein gesagt: Es liegt nichts, und der naechste
        // Versuch darf ohne Existenzpruefung hinausgehen.
        Assert.False(harness.Outbox.All()[0].OutcomeUnknown);
    }

    /// <summary>
    /// Ein Abbruch mitten im Senden hinterlässt einen Vermerk.
    /// </summary>
    /// <remarks>
    /// Strg+C sagt nichts darüber, ob die Anfrage den Server erreicht hat. Ohne diesen Vermerk
    /// bliebe der Eintrag als scheinbar geklärter Erstversuch stehen — und die nächste
    /// Wiederholung ginge ohne Existenzprüfung hinaus.
    /// </remarks>
    [Fact]
    public async Task Ein_Abbruch_mitten_im_Senden_hinterlaesst_einen_Vermerk()
    {
        using Harness harness = new();
        harness.Enqueue();
        harness.Repository.FailWith = new OperationCanceledException();

        QueuedCommit entry = harness.Outbox.All()[0];

        // Der Abbruch wird weitergereicht - ein verschluckter waere schlimmer.
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Uploader.SendAsync(entry));

        Assert.True(harness.Outbox.All()[0].OutcomeUnknown);
        Assert.Equal(1, harness.Outbox.All()[0].Attempts);
    }

    /// <summary>
    /// Vor der Wiederholung eines ungeklärten Eintrags steht die Existenzprüfung.
    /// </summary>
    [Fact]
    public async Task Ein_ungeklaerter_Eintrag_wird_erst_geprueft()
    {
        using Harness harness = new();
        harness.Enqueue();
        harness.Outbox.Fail(Sha, "Zeitüberschreitung.", outcomeUnknown: true);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        harness.Repository.Exists = true;

        FlushReport report = await harness.Uploader.FlushAsync();

        Assert.Equal(1, report.AlreadyThere);
        Assert.Equal(1, harness.Repository.Checked);

        // Es wurde NICHT gesendet: Die Fernwartung stand schon in TANSS.
        Assert.Equal(0, harness.Repository.Created);
        Assert.Equal(QueueState.Done, harness.Outbox.All()[0].State);
    }

    [Fact]
    public async Task Ist_die_Fernwartung_nicht_vorhanden_wird_gesendet()
    {
        using Harness harness = new();
        harness.Enqueue();
        harness.Outbox.Fail(Sha, "Zeitüberschreitung.", outcomeUnknown: true);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        harness.Repository.Exists = false;

        FlushReport report = await harness.Uploader.FlushAsync();

        Assert.Equal(1, report.Sent);
        Assert.Equal(1, harness.Repository.Checked);
        Assert.Equal(1, harness.Repository.Created);
    }

    /// <summary>
    /// <b>Die teuerste Zeile des ganzen Werkzeugs.</b> Scheitert die Existenzprüfung, heißt das
    /// „unbekannt“ und nicht „nicht vorhanden“ — es wird zurückgestellt, nicht gesendet.
    /// </summary>
    /// <remarks>
    /// Wer diese Fehlerbehandlung „vereinfacht“ und im Zweifel sendet, erzeugt Dubletten in der
    /// Produktivinstanz eines Kunden, die jemand von Hand aus der Datenbank schneiden muss.
    /// </remarks>
    [Fact]
    public async Task Eine_gescheiterte_Existenzpruefung_verhindert_das_Senden()
    {
        using Harness harness = new();
        harness.Enqueue();
        harness.Outbox.Fail(Sha, "Zeitüberschreitung.", outcomeUnknown: true);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        harness.Repository.ExistsFailsWith = new TanssUnreachableException("Immer noch nichts.");

        FlushReport report = await harness.Uploader.FlushAsync();

        Assert.Equal(1, report.Postponed);
        Assert.Equal(0, harness.Repository.Created);
        Assert.True(harness.Outbox.All()[0].OutcomeUnknown);
    }

    [Fact]
    public async Task Ein_Fehlschlag_beendet_den_Durchlauf_nicht()
    {
        using Harness harness = new();
        harness.Enqueue("aaa");
        harness.Enqueue("bbb");
        harness.Repository.FailOn = "aaa";

        FlushReport report = await harness.Uploader.FlushAsync();

        Assert.Equal(1, report.Failed);
        Assert.Equal(1, report.Sent);
    }

    [Fact]
    public async Task Ohne_Einrichtung_bleibt_der_Eintrag_liegen_und_der_Grund_steht_dabei()
    {
        using Harness harness = new();
        harness.Enqueue();
        harness.Repository.FailWith = new Storage.TokenStoreException("Kein Token hinterlegt.");

        FlushReport report = await harness.Uploader.FlushAsync();

        Assert.Equal(1, report.Failed);
        Assert.Contains("Kein Token", harness.Outbox.All()[0].LastError, StringComparison.Ordinal);
    }

    /// <summary>Alles, was ein Versandtest braucht — mit eigener Ablage und stehender Uhr.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _directory;

        public Harness()
        {
            _directory = Path.Combine(Path.GetTempPath(),
                "tanss-git-test-" + Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(_directory);

            Clock = new TestClock();
            Outbox = new CommitOutbox(Path.Combine(_directory, "queue.json"), Clock);
            Log = new ActivityLog(Path.Combine(_directory, "log.jsonl"), Clock);
            Repository = new FakeRemoteSupports();
            Uploader = new CommitUploader(Repository, Outbox, Log);
        }

        public TestClock Clock { get; }

        public CommitOutbox Outbox { get; }

        public ActivityLog Log { get; }

        public FakeRemoteSupports Repository { get; }

        public CommitUploader Uploader { get; }

        public void Enqueue(string sha = Sha) => Outbox.Enqueue(new RemoteSupportWrite
        {
            TypeId = 1007,
            EmployeeId = 42,
            StartTime = 1757800000,
            EndTime = 1757800900,
            RemoteMaintenanceId = sha,
            Comment = "Rechnungslauf korrigiert",
        }, "abrechnung", "Rechnungslauf korrigiert");

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Eine Attrappe der Fernwartungen: Sie zählt mit und scheitert auf Ansage.</summary>
    private sealed class FakeRemoteSupports : IRemoteSupportRepository
    {
        public int Created { get; private set; }

        public int Checked { get; private set; }

        public bool Exists { get; set; }

        public Exception? FailWith { get; set; }

        public Exception? ExistsFailsWith { get; set; }

        public string? FailOn { get; set; }

        public Task<RemoteSupportRead> CreateAsync(RemoteSupportWrite item, CancellationToken ct = default) =>
            Task.FromResult(CreateWithDiagnosticsAsync(item, ct).Result.Support);

        public Task<RemoteSupportCreateResult> CreateWithDiagnosticsAsync(RemoteSupportWrite item,
                                                                          CancellationToken ct = default)
        {
            if (FailWith is { } always)
            {
                throw always;
            }

            if (FailOn is { } sha && string.Equals(sha, item.RemoteMaintenanceId, StringComparison.Ordinal))
            {
                throw new TanssException("Abgewiesen.", 400);
            }

            Created++;

            return Task.FromResult(new RemoteSupportCreateResult(
                new RemoteSupportRead { Id = 38584, RemoteMaintenanceId = item.RemoteMaintenanceId },
                AttributionConfirmed: true, Warning: null));
        }

        public Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset around,
                                      CancellationToken ct = default) =>
            ExistsAsync(remoteMaintenanceId, around, around, ct);

        public Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset startedAt,
                                      DateTimeOffset endedAt, CancellationToken ct = default)
        {
            Checked++;

            return ExistsFailsWith is { } error
                ? throw error
                : Task.FromResult(Exists);
        }

        public Task<bool> ExistsAsync(RemoteSupportWrite item, CancellationToken ct = default) =>
            ExistsAsync(item.RemoteMaintenanceId, DateTimeOffset.UtcNow, ct);

        public Task<IReadOnlyList<RemoteSupportRead>> ListAsync(Timeframe timeframe,
                                                                string? text = null,
                                                                CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RemoteSupportRead>>([]);

        public Task<IReadOnlyList<RemoteSupportSystem>> ListSystemsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RemoteSupportSystem>>([]);
    }

    /// <summary>Eine Uhr, die stehen bleibt, bis ein Test sie weiterstellt.</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }
}
