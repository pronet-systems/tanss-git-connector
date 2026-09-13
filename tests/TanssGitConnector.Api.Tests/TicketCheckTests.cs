using System.Net;
using TanssGitConnector.Api.Http;
using TanssGitConnector.Api.Model;
using TanssGitConnector.Api.Repository;
using TanssGitConnector.Api.Tests.Fakes;
using Xunit;

namespace TanssGitConnector.Api.Tests;

/// <summary>
/// Die Prüfung einer Ticketnummer — und die drei Antworten, die sie kennt.
/// </summary>
/// <remarks>
/// Der ganze Zweck dieser Prüfung steckt im Unterschied zwischen „gibt es nicht“ und „konnte
/// nicht gefragt werden“. Wer beides gleich behandelt, buchte entweder ohne Ticket, wo eines
/// war, oder auf eine Nummer, die keines ist.
/// </remarks>
public class TicketCheckTests
{
    private static TanssOptions Options => new()
    {
        BaseUrl = "https://tanss.example.de/backend",
        EmployeeId = 42,
    };

    [Fact]
    public async Task Ohne_Nummer_wird_gar_nicht_gefragt()
    {
        RecordingHandler handler = new();
        using TanssClient client = new(Options, new TestToken(), null, handler);

        TicketCheck check = await new TicketRepository(client).CheckAsync(0);

        Assert.Equal(TicketCheckOutcome.NoTicketNumber, check.Outcome);
        Assert.Empty(handler.Requests);
        Assert.True(check.MayLink);
    }

    [Fact]
    public async Task Ein_vorhandenes_Ticket_bringt_Titel_und_Firma_mit()
    {
        RecordingHandler handler = new();
        handler.Answer(HttpStatusCode.OK, """
            {"meta":{"linkedEntities":{"companies":{"886":{"name":"Müller GmbH"}}}},
             "content":{"id":5000,"title":"Serverstörung","companyId":886}}
            """);

        using TanssClient client = new(Options, new TestToken(), null, handler);

        TicketCheck check = await new TicketRepository(client).CheckAsync(5000);

        Assert.Equal(TicketCheckOutcome.Exists, check.Outcome);
        Assert.Equal("#5000 Serverstörung — Müller GmbH", check.Summary);
        Assert.True(check.MayLink);
    }

    [Fact]
    public async Task Ein_404_mit_OBJECT_NOT_FOUND_heisst_gibt_es_nicht()
    {
        RecordingHandler handler = new();
        handler.Answer(HttpStatusCode.NotFound,
            """{"error":{"text":"OBJECT_NOT_FOUND","type":"DataNotFoundException"}}""");

        using TanssClient client = new(Options, new TestToken(), null, handler);

        TicketCheck check = await new TicketRepository(client).CheckAsync(999999999);

        Assert.Equal(TicketCheckOutcome.DoesNotExist, check.Outcome);
        Assert.False(check.MayLink);
    }

    /// <summary>
    /// Ein 404 ohne die Marke heißt „unbekannt“.
    /// </summary>
    /// <remarks>
    /// Die benutzten Routen sind überwiegend undokumentiert. Ein 404 kann ebenso gut eine
    /// Route sein, die es in dieser TANSS-Fassung nicht mehr gibt — dafür den Ticketbezug
    /// wegzulassen wäre falsch.
    /// </remarks>
    [Fact]
    public async Task Ein_404_ohne_Marke_heisst_unbekannt()
    {
        RecordingHandler handler = new();
        handler.Answer(HttpStatusCode.NotFound, "<html>404</html>");

        using TanssClient client = new(Options, new TestToken(), null, handler);

        TicketCheck check = await new TicketRepository(client).CheckAsync(5000);

        Assert.Equal(TicketCheckOutcome.Undetermined, check.Outcome);
        Assert.True(check.MayLink);
    }

    [Fact]
    public async Task Eine_misslungene_Pruefung_haelt_das_Buchen_nicht_auf()
    {
        RecordingHandler handler = new();
        handler.Answer(HttpStatusCode.Forbidden, """{"error":{"text":"NO_RIGHTS"}}""");

        using TanssClient client = new(Options, new TestToken(), RetryPolicy.None, handler);

        TicketCheck check = await new TicketRepository(client).CheckAsync(5000);

        Assert.Equal(TicketCheckOutcome.Undetermined, check.Outcome);
        Assert.True(check.MayLink);
    }

    /// <summary>
    /// Antwortet TANSS mit einem anderen Ticket, gilt die Antwort als ungeklärt.
    /// </summary>
    /// <remarks>
    /// Nicht gemessen, nur abgefangen. Fände es statt, wäre das angezeigte Ticket eine Lüge.
    /// </remarks>
    [Fact]
    public async Task Ein_fremdes_Ticket_in_der_Antwort_gilt_als_ungeklaert()
    {
        RecordingHandler handler = new();
        handler.AnswerContent("""{"id":5001,"title":"Ein anderes"}""");

        using TanssClient client = new(Options, new TestToken(), null, handler);

        TicketCheck check = await new TicketRepository(client).CheckAsync(5000);

        Assert.Equal(TicketCheckOutcome.Undetermined, check.Outcome);
    }
}
