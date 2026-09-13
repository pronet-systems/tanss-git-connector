using TanssGitConnector.Git;
using TanssGitConnector.Storage.Config;
using Xunit;

namespace TanssGitConnector.Storage.Tests;

/// <summary>
/// Die Konfiguration: was gelesen wird, was abgelehnt wird und was geprüft wird.
/// </summary>
public class ConfigTests
{
    private const string Minimal = """
        {
          "version": 1,
          "tanss": { "base_url": "https://tanss.kunde.de/backend", "employee_id": 42 },
          "commits": { "remote_support_type_id": 1007 }
        }
        """;

    [Fact]
    public void Das_Nötigste_genuegt()
    {
        AppConfig config = ConfigStore.Parse(Minimal);

        Assert.Equal(42, config.Tanss.EmployeeId);
        Assert.Equal(1007, config.Commits.RemoteSupportTypeId);

        // Die Vorgaben stehen im Quelltext und nicht in der Datei: Eine unveraenderte Datei
        // bekommt sie, statt einen Ladefehler.
        Assert.Equal(15, config.Commits.DurationMinutes);
        Assert.True(config.Commits.SkipMergeCommits);
        Assert.False(config.Commits.OnlyWithTicket);
        Assert.True(config.Tickets.Verify);
        Assert.Equal(30, config.Logging.RetentionDays);
    }

    /// <summary>
    /// Ein Tippfehler im Namen einer Einstellung ist ein Ladefehler.
    /// </summary>
    /// <remarks>
    /// Wer <c>only_with_tickets</c> statt <c>only_with_ticket</c> schreibt, hält seine
    /// Einschränkung für aktiv, während jeder Commit gebucht wird. Das ist schlimmer als ein
    /// Fehler beim Laden.
    /// </remarks>
    [Fact]
    public void Ein_unbekanntes_Feld_wird_abgelehnt()
    {
        ConfigException error = Assert.Throws<ConfigException>(() => ConfigStore.Parse("""
            {
              "version": 1,
              "tanss": { "base_url": "https://tanss.kunde.de/backend", "employee_id": 42 },
              "commits": { "remote_support_type_id": 1007, "only_with_tickets": true }
            }
            """));

        Assert.Contains("Unbekannte Felder", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Kommentare_und_nachgestellte_Kommata_sind_erlaubt()
    {
        AppConfig config = ConfigStore.Parse("""
            {
              // Die Datei wird von Hand gepflegt.
              "version": 1,
              "tanss": { "base_url": "https://tanss.kunde.de/backend", "employee_id": 42, },
              "commits": { "remote_support_type_id": 1007 },
            }
            """);

        Assert.Equal(42, config.Tanss.EmployeeId);
    }

    [Fact]
    public void Die_Dauerart_steht_als_Text_in_der_Datei()
    {
        AppConfig config = ConfigStore.Parse("""
            {
              "version": 1,
              "tanss": { "base_url": "https://tanss.kunde.de/backend", "employee_id": 42 },
              "commits": { "remote_support_type_id": 1007, "duration_mode": "since_last_commit" }
            }
            """);

        Assert.Equal(DurationMode.SinceLastCommit, config.Commits.DurationMode);
    }

    /// <summary>
    /// Eine Zahl als Dauerart wird nicht angenommen.
    /// </summary>
    /// <remarks>
    /// Eine verrutschte Zahl änderte stillschweigend die gebuchte Dauer — und niemand sähe es
    /// der Datei an.
    /// </remarks>
    [Fact]
    public void Eine_Zahl_als_Dauerart_wird_abgelehnt()
    {
        _ = Assert.Throws<ConfigException>(() => ConfigStore.Parse("""
            {
              "version": 1,
              "tanss": { "base_url": "https://tanss.kunde.de/backend", "employee_id": 42 },
              "commits": { "remote_support_type_id": 1007, "duration_mode": 1 }
            }
            """));
    }

    [Theory]
    [InlineData("https://tanss.kunde.de", "endet nicht auf /backend")]
    [InlineData("https://tanss.kunde.de/backend/", "Schrägstrich")]
    [InlineData("tanss.kunde.de/backend", "keine gültige Adresse")]
    public void Die_Basisadresse_wird_geprueft(string url, string expected)
    {
        AppConfig config = ConfigStore.Parse(Minimal) with
        {
            Tanss = new TanssSection { BaseUrl = url, EmployeeId = 42 },
        };

        Assert.Contains(ConfigValidator.Collect(config),
            problem => problem.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Ein_Fernwartungstyp_unter_1000_wird_beanstandet()
    {
        AppConfig config = ConfigStore.Parse(Minimal) with
        {
            Commits = new CommitsSection { RemoteSupportTypeId = 7 },
        };

        Assert.Contains(ConfigValidator.Collect(config),
            problem => problem.Contains("kleiner als 1000", StringComparison.Ordinal));
    }

    [Fact]
    public void Eine_Untergrenze_ueber_der_Obergrenze_wird_beanstandet()
    {
        AppConfig config = ConfigStore.Parse(Minimal) with
        {
            Commits = new CommitsSection
            {
                RemoteSupportTypeId = 1007,
                MinimumMinutes = 120,
                MaximumMinutes = 30,
            },
        };

        Assert.Contains(ConfigValidator.Collect(config),
            problem => problem.Contains("größer als", StringComparison.Ordinal));
    }

    /// <summary>
    /// Es werden <b>alle</b> Verstöße gesammelt, nicht nur der erste.
    /// </summary>
    /// <remarks>
    /// Wer eine Datei von Hand pflegt, soll sie in einem Durchgang richtigstellen können.
    /// </remarks>
    [Fact]
    public void Alle_Verstoesse_kommen_auf_einmal()
    {
        AppConfig config = new()
        {
            Version = 1,
            Tanss = new TanssSection { BaseUrl = "ftp://tanss", EmployeeId = 0 },
            Commits = new CommitsSection { RemoteSupportTypeId = 5 },
            Logging = new LoggingSection { Level = "laut" },
        };

        IReadOnlyList<string> problems = ConfigValidator.Collect(config);

        Assert.True(problems.Count >= 4, string.Join(" | ", problems));
    }

    [Fact]
    public void Geschrieben_und_wieder_gelesen_ergibt_dasselbe()
    {
        using Sandbox sandbox = new();
        ConfigStore store = new(Path.Combine(sandbox.Path, "config.json"));

        AppConfig written = ConfigStore.Parse(Minimal) with
        {
            Commits = new CommitsSection
            {
                RemoteSupportTypeId = 1007,
                DurationMode = DurationMode.SinceLastCommit,
                OnlyWithTicket = true,
            },
        };

        store.Save(written);
        AppConfig read = store.Load();

        Assert.Equal(DurationMode.SinceLastCommit, read.Commits.DurationMode);
        Assert.True(read.Commits.OnlyWithTicket);
        Assert.Equal(written.Tanss.BaseUrl, read.Tanss.BaseUrl);
    }

    [Fact]
    public void Eine_ungueltige_Konfiguration_wird_gar_nicht_erst_geschrieben()
    {
        using Sandbox sandbox = new();
        ConfigStore store = new(Path.Combine(sandbox.Path, "config.json"));

        AppConfig broken = ConfigStore.Parse(Minimal) with
        {
            Commits = new CommitsSection { RemoteSupportTypeId = 3 },
        };

        _ = Assert.Throws<ConfigValidationException>(() => store.Save(broken));
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void Ohne_Datei_nennt_die_Meldung_den_Einrichtungsbefehl()
    {
        using Sandbox sandbox = new();
        ConfigStore store = new(Path.Combine(sandbox.Path, "config.json"));

        ConfigException error = Assert.Throws<ConfigException>(store.Load);

        Assert.Contains("tanss-git setup", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Die_Beispielkonfiguration_ist_gueltig()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config.example.json");

        // Sie liegt neben dem Programm und wird in der Anleitung als Vorlage genannt. Eine
        // Vorlage, die sich nicht laden laesst, ist schlimmer als keine.
        AppConfig config = ConfigStore.Parse(File.ReadAllText(path), path);

        Assert.Empty(ConfigValidator.Collect(config));
    }
}
