using System.Net;
using System.Text.Json;
using TanssGitConnector.Api.Http;
using TanssGitConnector.Api.Model;
using TanssGitConnector.Api.Repository;
using TanssGitConnector.Api.Tests.Fakes;
using Xunit;

namespace TanssGitConnector.Api.Tests;

/// <summary>
/// Das Anlegen einer Fernwartung und die Existenzprüfung, ohne die nicht wiederholt wird.
/// </summary>
public class RemoteSupportTests
{
    private static TanssOptions Options => new()
    {
        BaseUrl = "https://tanss.example.de/backend",
        EmployeeId = 42,
    };

    private static RemoteSupportWrite Payload => new()
    {
        TypeId = 1007,
        EmployeeId = 42,
        StartTime = 1757800000,
        EndTime = 1757800900,
        RemoteMaintenanceId = "3f2a1bc9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f4a3",
        Comment = "Rechnungslauf korrigiert",
    };

    [Fact]
    public async Task Angelegt_wird_auf_tanss_x_per_POST()
    {
        RecordingHandler handler = new();
        handler.AnswerContent("""{"id":38584,"remoteMaintenanceId":"3f2a1bc"}""");

        using TanssClient client = new(Options, new TestToken(), null, handler);
        RemoteSupportRepository repository = new(client, 42);

        RemoteSupportRead created = await repository.CreateAsync(Payload);

        Assert.Equal(38584, created.Id);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.EndsWith("/api/tanss.x/v1/remoteSupports", handler.LastUri.AbsolutePath,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// Jedes Feld geht mit, auch die Nullen.
    /// </summary>
    /// <remarks>
    /// TANSS unterscheidet zwischen „Feld fehlt“ und „Feld ist 0“: Ein weggelassenes
    /// <c>endTime</c> ist nicht dasselbe wie <c>endTime: 0</c> — letzteres heißt „läuft noch“.
    /// </remarks>
    [Fact]
    public async Task Der_Rumpf_traegt_die_Pflichtfelder()
    {
        RecordingHandler handler = new();
        handler.AnswerContent("""{"id":1}""");

        using TanssClient client = new(Options, new TestToken(), null, handler);
        RemoteSupportRepository repository = new(client, 42);

        _ = await repository.CreateAsync(Payload);

        using JsonDocument body = JsonDocument.Parse(handler.Bodies[0]);
        JsonElement root = body.RootElement;

        Assert.Equal(1007, root.GetProperty("typeId").GetInt32());
        Assert.Equal(42, root.GetProperty("employeeId").GetInt32());
        Assert.Equal(1757800900, root.GetProperty("endTime").GetInt64());
        Assert.Equal(0, root.GetProperty("ticketId").GetInt32());
        Assert.Equal(Payload.RemoteMaintenanceId, root.GetProperty("remoteMaintenanceId").GetString());

        // Nie senden: id, fee, typeName - sie koennten serverseitig auf etwas Bestehendes binden
        // oder sind reine Anzeigefelder.
        Assert.False(root.TryGetProperty("id", out _));
        Assert.False(root.TryGetProperty("fee", out _));
        Assert.False(root.TryGetProperty("typeName", out _));

        // deviceId und companyId gehen nur mit, wenn sie gesetzt sind: Eine leere Kennung ist
        // keine Kennung, und eine gesendete companyId 0 koennte eine vorhandene Zuordnung
        // ueberschreiben.
        Assert.False(root.TryGetProperty("deviceId", out _));
        Assert.False(root.TryGetProperty("companyId", out _));
    }

    [Fact]
    public async Task Ein_Schreibfehler_wird_nicht_wiederholt()
    {
        RecordingHandler handler = new();
        handler.Answer(HttpStatusCode.InternalServerError, "{}");

        using TanssClient client = new(Options, new TestToken(),
            RetryPolicy.Default with { Sleep = (_, _) => Task.CompletedTask }, handler);

        RemoteSupportRepository repository = new(client, 42);

        _ = await Assert.ThrowsAsync<TanssException>(() => repository.CreateAsync(Payload));

        // Genau ein Versuch. TANSS dedupliziert nicht - eine Wiederholung erzeugte einen
        // zweiten Datensatz, der nur per Datenbankzugriff wieder wegzubekommen ist.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Ein_zu_kleiner_Typ_wird_als_solcher_gemeldet()
    {
        RecordingHandler handler = new();
        handler.Answer(HttpStatusCode.BadRequest, """{"error":{"text":"TYPE_GREATER_1000"}}""");

        using TanssClient client = new(Options, new TestToken(), null, handler);
        RemoteSupportRepository repository = new(client, 42);

        TanssRemoteSupportTypeException error =
            await Assert.ThrowsAsync<TanssRemoteSupportTypeException>(
                () => repository.CreateAsync(Payload with { TypeId = 3 }));

        Assert.Contains("1000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Die_Existenzpruefung_vergleicht_die_Kennung_genau()
    {
        RecordingHandler handler = new();

        // Der Textfilter von TANSS durchsucht comment UND remoteMaintenanceId. Ein Treffer
        // allein genuegt deshalb nicht - sonst gaelte eine Kennung, die zufaellig in einem
        // fremden Kommentar steht, als eigener Upload.
        handler.AnswerContent("""
            [{"id":1,"remoteMaintenanceId":"ein-anderer-hash","comment":"3f2a1bc9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f4a3"}]
            """);

        using TanssClient client = new(Options, new TestToken(), null, handler);
        RemoteSupportRepository repository = new(client, 42);

        Assert.False(await repository.ExistsAsync(Payload));
    }

    [Fact]
    public async Task Die_Existenzpruefung_findet_den_eigenen_Datensatz()
    {
        RecordingHandler handler = new();
        handler.AnswerContent($$"""
            [{"id":1,"remoteMaintenanceId":"{{Payload.RemoteMaintenanceId}}"}]
            """);

        using TanssClient client = new(Options, new TestToken(), null, handler);
        RemoteSupportRepository repository = new(client, 42);

        Assert.True(await repository.ExistsAsync(Payload));
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
    }

    /// <summary>
    /// Eine unerwartete Antwortform ist ein Fehler und keine leere Liste.
    /// </summary>
    /// <remarks>
    /// Eine fälschlich leere Liste hiesse „nicht vorhanden“, die Existenzprüfung gäbe grünes
    /// Licht — und weil TANSS nicht dedupliziert, stünde die Fernwartung anschließend zweimal
    /// in der Abrechnung.
    /// </remarks>
    [Fact]
    public async Task Eine_unerwartete_Antwortform_ist_ein_Fehler()
    {
        RecordingHandler handler = new();
        handler.AnswerContent("""{"eintraege":[]}""");

        using TanssClient client = new(Options, new TestToken(),
            RetryPolicy.None, handler);

        RemoteSupportRepository repository = new(client, 42);

        TanssException error = await Assert.ThrowsAsync<TanssException>(
            () => repository.ExistsAsync(Payload));

        Assert.Contains("dedupliziert nicht", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bleibt die Attribution unbestätigt, wird gemeldet und nicht geworfen.
    /// </summary>
    /// <remarks>
    /// Ein geworfener Fehler würde den Aufrufer zur Wiederholung verleiten — und die erzeugte
    /// einen zweiten Datensatz. Der erste ist ja angelegt.
    /// </remarks>
    [Fact]
    public async Task Eine_unbestaetigte_Attribution_wird_gemeldet_nicht_geworfen()
    {
        RecordingHandler handler = new();
        handler.Answer(HttpStatusCode.OK, """{"meta":{},"content":{"id":38584}}""");

        using TanssClient client = new(Options, new TestToken(), null, handler);
        RemoteSupportRepository repository = new(client, 42);

        RemoteSupportCreateResult result = await repository.CreateWithDiagnosticsAsync(Payload);

        Assert.False(result.AttributionConfirmed);
        Assert.NotNull(result.Warning);
        Assert.Contains("Nicht erneut senden", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Eine_bestaetigte_Attribution_meldet_nichts()
    {
        RecordingHandler handler = new();
        handler.Answer(HttpStatusCode.OK,
            """{"meta":{"linkedEntities":{"employees":{"42":{"name":"Sebastian Michel"}}}},"content":{"id":38584}}""");

        using TanssClient client = new(Options, new TestToken(), null, handler);
        RemoteSupportRepository repository = new(client, 42);

        RemoteSupportCreateResult result = await repository.CreateWithDiagnosticsAsync(Payload);

        Assert.True(result.AttributionConfirmed);
        Assert.Null(result.Warning);
    }
}
