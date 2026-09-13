using System.Net;
using TanssGitConnector.Api.Http;
using TanssGitConnector.Api.Model;
using TanssGitConnector.Api.Tests.Fakes;
using Xunit;

namespace TanssGitConnector.Api.Tests;

/// <summary>
/// Die Netzschicht: Präfixregel, Kopfzeile, Reihenfolge von Status und Rumpf.
/// </summary>
/// <remarks>
/// Das sind die drei Stellen, an denen der Umgang mit TANSS am häufigsten scheitert — und alle
/// drei lassen sich ohne Instanz prüfen.
/// </remarks>
public class TanssClientTests
{
    private static TanssOptions Options => new()
    {
        BaseUrl = "https://tanss.example.de/backend",
        EmployeeId = 42,
    };

    [Fact]
    public async Task Auf_api_v1_haengt_der_Client_loggedInUserId_selbst_an()
    {
        RecordingHandler handler = new();
        handler.AnswerContent("[]");

        using TanssClient client = new(Options, new TestToken(), null, handler);
        _ = await client.GetAsync<List<Ticket>>("/api/v1/tickets/own");

        Assert.Contains("loggedInUserId=42", handler.LastUri.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Auf <c>tanss.x</c> geht der Parameter nicht mit — auch dann nicht, wenn ein Aufrufer ihn
    /// selbst setzt.
    /// </summary>
    /// <remarks>
    /// Dort ist er überflüssig, und Überflüssiges gehört nicht auf die Leitung. Die Regel gilt
    /// in beide Richtungen, damit sie an genau einer Stelle steht.
    /// </remarks>
    [Fact]
    public async Task Auf_tanss_x_geht_loggedInUserId_nicht_mit()
    {
        RecordingHandler handler = new();
        handler.AnswerContent("[]");

        using TanssClient client = new(Options, new TestToken(), null, handler);

        Dictionary<string, string?> query = new(StringComparer.Ordinal)
        {
            ["LoggedInUserId"] = "99",
        };

        _ = await client.GetAsync<List<RemoteSupportSystem>>(TanssRoutes.RemoteSupportSystems, query);

        Assert.DoesNotContain("oggedInUserId", handler.LastUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Das_Token_geht_als_apiToken_hinaus_und_nicht_als_Authorization()
    {
        RecordingHandler handler = new();
        handler.AnswerContent("[]");

        using TanssClient client = new(Options, new TestToken("Bearer probe"), null, handler);
        _ = await client.GetAsync<List<RemoteSupportSystem>>(TanssRoutes.RemoteSupportSystems);

        Assert.True(handler.Requests[^1].Headers.TryGetValues("apiToken", out IEnumerable<string>? values));
        Assert.Equal("Bearer probe", values!.Single());
        Assert.False(handler.Requests[^1].Headers.Contains("Authorization"));
    }

    /// <summary>
    /// Eine leere 403 ist kein leerer Erfolg.
    /// </summary>
    /// <remarks>
    /// Die wichtigste Zeile Verhalten in der Netzschicht: Erst der Status, dann der Rumpf.
    /// Andersherum hätte das Werkzeug den Commit als gebucht abgehakt, obwohl TANSS ihn
    /// abgewiesen hat.
    /// </remarks>
    [Fact]
    public async Task Eine_leere_403_ist_kein_leerer_Erfolg()
    {
        RecordingHandler handler = new();
        handler.Answer(HttpStatusCode.Forbidden, string.Empty);

        using TanssClient client = new(Options, new TestToken(), null, handler);

        _ = await Assert.ThrowsAsync<TanssAuthException>(
            () => client.GetAsync<List<RemoteSupportSystem>>(TanssRoutes.RemoteSupportSystems));
    }

    [Fact]
    public async Task Ein_404_mit_OBJECT_NOT_FOUND_wird_zu_NotFound()
    {
        RecordingHandler handler = new();
        handler.Answer(HttpStatusCode.NotFound,
            """{"error":{"text":"OBJECT_NOT_FOUND","type":"DataNotFoundException"}}""");

        using TanssClient client = new(Options, new TestToken(), null, handler);

        _ = await Assert.ThrowsAsync<TanssNotFoundException>(
            () => client.GetAsync<Ticket>(TanssRoutes.TicketById(999999999)));
    }

    [Fact]
    public void Eine_Basisadresse_ohne_Schema_ist_ein_Einstellungsfehler()
    {
        RecordingHandler handler = new();
        using TanssClient client = new(Options with { BaseUrl = "tanss.example.de/backend" },
                                       new TestToken(), null, handler);

        TanssConfigurationException error = Assert.Throws<TanssConfigurationException>(
            () => client.BuildUri("/api/v1/tickets/own"));

        Assert.Contains("/backend", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Das Prägen eines Tokens wird niemals wiederholt.
    /// </summary>
    /// <remarks>
    /// Das Verb ist GET, der Vorgang ist es nicht: Jeder Aufruf stellt ein Token aus, und TANSS
    /// 10.10.0 kennt keinen Widerruf. Eine Wiederholung nach einer Zeitüberschreitung prägte ein
    /// zweites, von dem niemand mehr erführe.
    /// </remarks>
    [Fact]
    public async Task Das_Praegen_laeuft_mit_genau_einem_Versuch()
    {
        RecordingHandler handler = new();
        handler.Answer(HttpStatusCode.InternalServerError, "{}");

        using TanssClient client = new(Options, new TestToken(),
            RetryPolicy.Default with { MaxAttempts = 5, Sleep = (_, _) => Task.CompletedTask },
            handler);

        _ = await Assert.ThrowsAsync<TanssException>(
            () => client.GetAsync<object>(TanssRoutes.MintToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Ein_gewoehnlicher_Lesezugriff_wird_wiederholt()
    {
        RecordingHandler handler = new();
        handler.Answer(HttpStatusCode.InternalServerError, "{}");
        handler.AnswerContent("[]");

        using TanssClient client = new(Options, new TestToken(),
            RetryPolicy.Default with { Sleep = (_, _) => Task.CompletedTask }, handler);

        _ = await client.GetAsync<List<RemoteSupportSystem>>(TanssRoutes.RemoteSupportSystems);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("/api/v1/tickets/own", true)]
    [InlineData("/api/tanss.x/v1/technicians", false)]
    public void Die_Praefixregel_steht_an_einer_Stelle(string path, bool expected) =>
        Assert.Equal(expected, TanssRoutes.NeedsLoggedInUserId(path));

    [Theory]
    [InlineData("/api/v1/jwts/tanss_app", true)]
    [InlineData("/api/v1/jwts/irgendwas_neues", true)]
    [InlineData("/api/v1/tickets/own", false)]
    public void Seiteneffekte_haengen_am_Praefix_und_nicht_an_einer_Route(string path,
                                                                         bool expected) =>
        Assert.Equal(expected, TanssRoutes.HasSideEffectOnGet(path));
}
