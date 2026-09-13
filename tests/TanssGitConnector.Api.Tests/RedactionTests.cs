using TanssGitConnector.Api.Diagnostics;
using Xunit;

namespace TanssGitConnector.Api.Tests;

/// <summary>
/// Die Schwärzung: Sie muss Geheimnisse treffen und Begründungen stehen lassen.
/// </summary>
/// <remarks>
/// Beide Seiten sind wichtig. Ein Muster, das aus „Der Token: abgelaufen, bitte erneuern.“ ein
/// „Der Token: &lt;geschwaerzt&gt;, bitte erneuern.“ macht, schwärzt kein Geheimnis, sondern die
/// Begründung — und die braucht man am Freitagnachmittag.
/// </remarks>
public class RedactionTests
{
    [Fact]
    public void Ein_JWT_verschwindet()
    {
        string scrubbed = Redaction.Scrub(
            "Anfrage mit eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiI0MiJ9.abcDEF-_123 abgewiesen");

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", scrubbed, StringComparison.Ordinal);
        Assert.Contains(Redaction.Mask, scrubbed, StringComparison.Ordinal);
        Assert.Contains("abgewiesen", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_Bearer_Wert_verschwindet()
    {
        string scrubbed = Redaction.Scrub("""{"apiToken":"Bearer geheim-123"}""");

        Assert.DoesNotContain("geheim-123", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_Kennwort_in_einer_Abfragezeichenkette_verschwindet()
    {
        string scrubbed = Redaction.Scrub("login?password=kennwort&user=michel");

        Assert.DoesNotContain("kennwort", scrubbed, StringComparison.Ordinal);
        Assert.Contains("user=michel", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Eine_Begruendung_bleibt_lesbar()
    {
        const string sentence = "Der Token: abgelaufen, bitte erneuern.";

        Assert.Equal(sentence, Redaction.Scrub(sentence));
    }

    [Fact]
    public void Leer_bleibt_leer()
    {
        Assert.Equal(string.Empty, Redaction.Scrub((string?)null));
        Assert.Equal(string.Empty, Redaction.Scrub(string.Empty));
    }
}
