using TanssGitConnector.Storage.Config;
using Xunit;

namespace TanssGitConnector.Cli.Tests;

/// <summary>
/// Was auf einem Rechner ohne Einrichtung geschieht.
/// </summary>
/// <remarks>
/// Die Zusage lautet: Kein Befehl stürzt ab, jeder nennt die Einrichtung — und der Haken
/// schweigt fast und endet mit 0. Sie ist nur etwas wert, wenn sie geprüft wird, und prüfen
/// liesse sie sich sonst nur, indem ein Test das Benutzerprofil des Ausführenden leerräumt.
/// </remarks>
public class WithoutConfigurationTests
{
    /// <summary>
    /// Der Haken endet mit 0, auch ohne jede Einrichtung.
    /// </summary>
    /// <remarks>
    /// Wenn er läuft, ist der Commit bereits geschrieben. Ein Rückgabewert ungleich 0 sähe für
    /// den Techniker nach einem misslungenen Commit aus.
    /// </remarks>
    [Fact]
    public async Task Der_Haken_endet_mit_Null_und_nennt_die_Einrichtung()
    {
        using Sandbox sandbox = new();
        StringWriter output = new();
        StringWriter error = new();

        int code = await Program.RunAsync(["hook"], sandbox.Store, TextReader.Null, output, error);

        Assert.Equal(ExitCode.Healthy, code);
        Assert.Contains("tanss-git setup", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Der_Haken_schweigt_mit_quiet()
    {
        using Sandbox sandbox = new();
        StringWriter output = new();
        StringWriter error = new();

        int code = await Program.RunAsync(["hook", "--quiet"], sandbox.Store, TextReader.Null,
                                          output, error);

        Assert.Equal(ExitCode.Healthy, code);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("status")]
    [InlineData("queue")]
    [InlineData("types")]
    public async Task Jeder_andere_Befehl_nennt_die_Einrichtung_und_meldet_gestoert(string command)
    {
        using Sandbox sandbox = new();
        StringWriter output = new();
        StringWriter error = new();

        int code = await Program.RunAsync([command], sandbox.Store, TextReader.Null, output, error);

        Assert.Equal(ExitCode.Broken, code);
        Assert.Contains("noch nicht eingerichtet", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(sandbox.Store.Path, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Die_Hilfe_laeuft_auch_ohne_Einrichtung()
    {
        using Sandbox sandbox = new();
        StringWriter output = new();

        int code = await Program.RunAsync(["--help"], sandbox.Store, TextReader.Null, output,
                                          TextWriter.Null);

        Assert.Equal(ExitCode.Healthy, code);
        Assert.Contains("tanss-git", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ein_Aufruffehler_endet_mit_64()
    {
        using Sandbox sandbox = new();

        int code = await Program.RunAsync(["quatsch"], sandbox.Store, TextReader.Null,
                                          TextWriter.Null, new StringWriter());

        Assert.Equal(ExitCode.Usage, code);
    }

    /// <summary>
    /// Eine kaputte Konfiguration nennt den Grund — und meldet nicht „nicht vorhanden“.
    /// </summary>
    [Fact]
    public async Task Eine_kaputte_Konfiguration_nennt_den_Grund()
    {
        using Sandbox sandbox = new();
        await File.WriteAllTextAsync(sandbox.Store.Path, "{ kein JSON");

        StringWriter error = new();
        int code = await Program.RunAsync(["status"], sandbox.Store, TextReader.Null,
                                          TextWriter.Null, error);

        Assert.Equal(ExitCode.Broken, code);
        Assert.Contains("kein gültiges JSON", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class Sandbox : IDisposable
    {
        private readonly string _directory;

        public Sandbox()
        {
            _directory = Path.Combine(Path.GetTempPath(),
                "tanss-git-test-" + Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(_directory);
            Store = new ConfigStore(Path.Combine(_directory, "config.json"));
        }

        public ConfigStore Store { get; }

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
}
