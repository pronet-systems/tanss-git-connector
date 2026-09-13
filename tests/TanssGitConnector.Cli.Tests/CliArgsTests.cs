using TanssGitConnector.Cli.CommandLine;
using Xunit;

namespace TanssGitConnector.Cli.Tests;

/// <summary>
/// Die Auswertung der Befehlszeile — sie wirft nie und sagt immer, was erwartet wird.
/// </summary>
public class CliArgsTests
{
    [Fact]
    public void Ohne_Argumente_kommt_ein_Aufruffehler_mit_Liste()
    {
        CliArgs parsed = CliArgs.Parse([]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("setup", parsed.Error, StringComparison.Ordinal);
        Assert.Contains("hook", parsed.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("doctor", CliCommand.Doctor)]
    [InlineData("hook", CliCommand.Hook)]
    [InlineData("enable", CliCommand.Enable)]
    [InlineData("queue", CliCommand.Queue)]
    [InlineData("types", CliCommand.Types)]
    public void Die_Befehle_werden_erkannt(string word, CliCommand expected)
    {
        CliArgs parsed = CliArgs.Parse([word]);

        Assert.Null(parsed.Error);
        Assert.Equal(expected, parsed.Command);
    }

    [Fact]
    public void Ein_unbekannter_Befehl_nennt_die_bekannten()
    {
        CliArgs parsed = CliArgs.Parse(["quatsch"]);

        Assert.Contains("Unbekannter Befehl", parsed.Error, StringComparison.Ordinal);
        Assert.Contains("doctor", parsed.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Hilfe_wird_erkannt(string word) => Assert.True(CliArgs.Parse([word]).HelpRequested);

    [Fact]
    public void Hilfe_zu_einem_Befehl_wird_erkannt() =>
        Assert.True(CliArgs.Parse(["queue", "--help"]).HelpRequested);

    [Fact]
    public void Schalter_gelten_nur_bei_ihrem_Befehl()
    {
        Assert.True(CliArgs.Parse(["queue", "--flush"]).Flush);

        // "--flush" bei "doctor" ist ein Aufruffehler und kein stillschweigend uebergangener
        // Schalter: Wer ihn tippt, erwartet eine Wirkung.
        Assert.NotNull(CliArgs.Parse(["doctor", "--flush"]).Error);
    }

    [Fact]
    public void Das_Repository_laesst_sich_angeben()
    {
        CliArgs parsed = CliArgs.Parse(["hook", "--repository", "/pfad/zum/projekt"]);

        Assert.Equal("/pfad/zum/projekt", parsed.Repository);
        Assert.Null(parsed.Error);
    }

    /// <summary>
    /// Ein fehlender Wert wird als solcher gemeldet.
    /// </summary>
    /// <remarks>
    /// Sonst entstünde aus <c>--repository --force</c> ein Repository namens <c>--force</c>, und
    /// der Befehl liefe gegen ein Verzeichnis, das es nicht gibt.
    /// </remarks>
    [Fact]
    public void Ein_fehlender_Wert_ist_ein_Aufruffehler()
    {
        Assert.NotNull(CliArgs.Parse(["hook", "--repository"]).Error);
        Assert.NotNull(CliArgs.Parse(["enable", "--repository", "--force"]).Error);
    }

    [Fact]
    public void Die_Ticketnummer_muss_eine_Zahl_sein()
    {
        Assert.Equal(5000, CliArgs.Parse(["book", "HEAD", "--ticket", "5000"]).TicketId);
        Assert.NotNull(CliArgs.Parse(["book", "HEAD", "--ticket", "abc"]).Error);
        Assert.NotNull(CliArgs.Parse(["book", "HEAD", "--ticket", "0"]).Error);
    }

    [Fact]
    public void Book_nimmt_die_Fassung_als_freies_Wort()
    {
        CliArgs parsed = CliArgs.Parse(["book", "HEAD~2", "--dry-run"]);

        Assert.Equal("HEAD~2", parsed.Revision);
        Assert.True(parsed.DryRun);
    }

    [Fact]
    public void Token_kennt_genau_zwei_Unterbefehle()
    {
        Assert.Equal("rotate", CliArgs.Parse(["token", "rotate"]).SubCommand);
        Assert.Equal("status", CliArgs.Parse(["token", "status"]).SubCommand);
        Assert.NotNull(CliArgs.Parse(["token", "erneuern"]).Error);
    }

    [Fact]
    public void Unbekannte_Optionen_werden_gemeldet_und_nicht_uebergangen()
    {
        CliArgs parsed = CliArgs.Parse(["hook", "--schnell"]);

        Assert.Contains("--schnell", parsed.Error, StringComparison.Ordinal);
    }
}
