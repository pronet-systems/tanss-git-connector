using System.Net;
using System.Text;
using TanssGitConnector.Api.Contract;

namespace TanssGitConnector.Api.Tests.Fakes;

/// <summary>
/// Eine Attrappe der Verbindungsschicht: Sie merkt sich jede Anfrage und antwortet vorgegeben.
/// </summary>
/// <remarks>
/// <b>Wer eine Annahme über TANSS gegen seine eigene Nachbildung dieser Annahme prüft, bekommt
/// immer recht.</b> Diese Attrappe taugt deshalb nur für das, was <i>dieses</i> Werkzeug tut —
/// welche Adresse es baut, welche Kopfzeile es setzt, wie es eine Antwort liest. Was TANSS
/// tatsächlich antwortet, ist hier gesetzt und nicht bewiesen.
/// </remarks>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _answers = new();

    /// <summary>Die Anfragen in der Reihenfolge, in der sie kamen.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Die Rümpfe der Anfragen, in derselben Reihenfolge.</summary>
    public List<string> Bodies { get; } = [];

    /// <summary>Legt eine Antwort in die Reihe.</summary>
    public RecordingHandler Answer(HttpStatusCode status, string payload)
    {
        _answers.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });

        return this;
    }

    /// <summary>Legt eine erfolgreiche Antwort mit TANSS-Umschlag in die Reihe.</summary>
    public RecordingHandler AnswerContent(string content) =>
        Answer(HttpStatusCode.OK, $$"""{"meta":{},"content":{{content}}}""");

    /// <summary>Die zuletzt gebaute Adresse.</summary>
    public Uri LastUri => Requests[^1].RequestUri!;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                 CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        return _answers.Count > 0
            ? _answers.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"meta":{},"content":null}""", Encoding.UTF8,
                                            "application/json"),
            };
    }
}

/// <summary>Ein Tokenspeicher für Tests.</summary>
internal sealed class TestToken : ITokenStore
{
    private string _token;

    public TestToken(string token = "Bearer eyJhbGciOiJIUzI1NiJ9.eyJleHAiOjk5OTk5OTk5OTl9.x") =>
        _token = token;

    public string Read() => _token;

    public void Write(string token) => _token = token;
}
