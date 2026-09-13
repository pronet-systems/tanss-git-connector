using Xunit;

namespace TanssGitConnector.Git.Tests;

/// <summary>
/// Die Zerlegung der <c>git log</c>-Ausgabe.
/// </summary>
/// <remarks>
/// Die Fälle hier sind die, die man im Alltag selten erzeugt und die trotzdem vorkommen: ein
/// mehrzeiliger Rumpf, ein Wurzelcommit ohne Eltern, ein leerer Betreff. Gegen ein echtes
/// Repository wären sie mühsam herzustellen — hier kosten sie drei Zeilen.
/// </remarks>
public class CommitParsingTests
{
    private const char Separator = '\u001f';

    private static string Line(string sha, string parents, string seconds, string author,
                               string mail, string subject, string body) =>
        string.Join(Separator, sha, parents, seconds, author, mail, subject, body);

    [Fact]
    public void Liest_alle_sieben_Felder()
    {
        CommitInfo commit = GitRepository.Parse(Line(
            "3f2a1bc9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f4a3", "aaa111", "1757800000",
            "Erika Mustermann", "e.mustermann@example.de", "Rechnungslauf korrigiert",
            "Rechnungslauf korrigiert\n\nDer Stundensatz kam aus dem falschen Vertrag."));

        Assert.Equal("3f2a1bc9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f4a3", commit.Sha);
        Assert.Equal("3f2a1bc", commit.ShortSha);
        Assert.Equal("Rechnungslauf korrigiert", commit.Subject);
        Assert.Equal("Der Stundensatz kam aus dem falschen Vertrag.", commit.Body);
        Assert.Equal("e.mustermann@example.de", commit.AuthorEmail);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1757800000), commit.CommittedAt);
    }

    [Fact]
    public void Wurzelcommit_ohne_Eltern_ist_keine_Zusammenfuehrung()
    {
        CommitInfo commit = GitRepository.Parse(
            Line("abc", string.Empty, "1757800000", "A", "a@b.c", "Erster Commit", "Erster Commit"));

        Assert.False(commit.IsMerge);
        Assert.Equal(string.Empty, commit.Parents);
    }

    [Fact]
    public void Zwei_Eltern_sind_eine_Zusammenfuehrung()
    {
        CommitInfo commit = GitRepository.Parse(
            Line("abc", "aaa bbb", "1757800000", "A", "a@b.c", "Merge", "Merge"));

        Assert.True(commit.IsMerge);
    }

    [Fact]
    public void Mehrzeiliger_Rumpf_bleibt_vollstaendig()
    {
        string body = "Betreff\n\nErste Zeile.\nZweite Zeile.\n\nDritte.";

        CommitInfo commit = GitRepository.Parse(
            Line("abc", "aaa", "1757800000", "A", "a@b.c", "Betreff", body));

        Assert.Equal("Erste Zeile.\nZweite Zeile.\n\nDritte.", commit.Body);
    }

    [Fact]
    public void Ohne_Rumpf_bleibt_der_Rumpf_leer()
    {
        CommitInfo commit = GitRepository.Parse(
            Line("abc", "aaa", "1757800000", "A", "a@b.c", "Nur Betreff", "Nur Betreff"));

        Assert.Equal(string.Empty, commit.Body);
    }

    /// <summary>
    /// Eine Ausgabe mit zu wenigen Feldern wird abgewiesen — und nicht halb gelesen.
    /// </summary>
    /// <remarks>
    /// Halb gelesen hiesse: ein Zeitpunkt aus einem Feld, das etwas anderes bedeutet. Daraus
    /// entstünde gebuchte Arbeitszeit, die nie stattgefunden hat.
    /// </remarks>
    [Fact]
    public void Zu_wenige_Felder_werden_abgewiesen()
    {
        GitException error = Assert.Throws<GitException>(
            () => GitRepository.Parse("abc" + Separator + "aaa"));

        Assert.Contains("sieben", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_Zeitpunkt_der_keine_Unixsekunden_ist_wird_abgewiesen()
    {
        GitException error = Assert.Throws<GitException>(() => GitRepository.Parse(
            Line("abc", "aaa", "2026-09-13", "A", "a@b.c", "Betreff", "Betreff")));

        Assert.Contains("Unix-Sekunden", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/home/x/projekt", "projekt")]
    [InlineData("C:/Projekte/kunde-abrechnung", "kunde-abrechnung")]
    [InlineData("C:\\Projekte\\kunde", "kunde")]
    [InlineData("/home/x/projekt/", "projekt")]
    public void Repositoryname_kommt_aus_dem_Wurzelverzeichnis(string top, string expected) =>
        Assert.Equal(expected, GitRepository.NameOf(top));
}
