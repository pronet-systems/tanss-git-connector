using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TanssGitConnector.Storage.Config;

/// <summary>
/// Die Konfiguration auf der Platte.
/// </summary>
/// <remarks>
/// <para>Geschrieben wird ausschließlich über <see cref="AtomicFile"/>. Die Zieldatei wird
/// niemals geöffnet und teilweise überschrieben — ein Abbruch mitten im Schreiben liesse sonst
/// eine halbe Datei zurück, und der nächste Commit fände weder die alte noch die neue
/// Einstellung vor.</para>
/// <para>Gelesen wird bei <b>jedem</b> Aufruf des Hakens neu. Das kostet einen Dateizugriff je
/// Commit und ist es wert: Wer eine Einstellung ändert, will sie beim nächsten Commit wirksam
/// sehen und nicht erst nach einem Neustart von irgendetwas.</para>
/// </remarks>
public sealed class ConfigStore
{
    /// <summary>Öffnet eine Konfiguration an einem beliebigen Pfad.</summary>
    public ConfigStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>Öffnet die Konfiguration am vorgesehenen Ort im Benutzerprofil.</summary>
    public static ConfigStore Default() => new(StoragePaths.ConfigFile);

    /// <summary>Der vollständige Pfad der Datei.</summary>
    public string Path { get; }

    /// <summary>Liegt schon eine Konfiguration?</summary>
    public bool Exists() => File.Exists(Path);

    /// <summary>Lädt und prüft die Konfiguration.</summary>
    /// <exception cref="ConfigException">Die Datei fehlt, ist unlesbar oder kein gültiges JSON.</exception>
    /// <exception cref="ConfigValidationException">Die Datei ist lesbar, verletzt aber Regeln.</exception>
    public AppConfig Load()
    {
        if (!Exists())
        {
            throw new ConfigException(
                $"Keine Konfiguration unter {Path}. "
                + "Einrichtung starten mit: tanss-git setup");
        }

        string raw;
        try
        {
            raw = File.ReadAllText(Path, Encoding.UTF8);
        }
        catch (IOException exception)
        {
            throw new ConfigException($"{Path} liess sich nicht lesen: {exception.Message}", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ConfigException(
                $"{Path} liess sich nicht lesen: {exception.Message}. Läuft das Werkzeug unter "
                + "demselben Benutzerkonto wie bei der Einrichtung?", exception);
        }

        AppConfig config = Parse(raw, Path);
        ConfigValidator.Validate(config, Path);
        return config;
    }

    /// <summary>
    /// Liest eine Konfiguration aus einer Zeichenkette — ohne Dateizugriff, aber mit denselben
    /// Meldungen.
    /// </summary>
    /// <param name="raw">Der JSON-Text.</param>
    /// <param name="origin">Herkunft für die Fehlermeldung, üblicherweise ein Dateipfad.</param>
    public static AppConfig Parse(string raw, string? origin = null)
    {
        ArgumentNullException.ThrowIfNull(raw);

        string where = string.IsNullOrWhiteSpace(origin) ? "Die Konfiguration" : origin;

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(raw, ConfigJson.Options)
                ?? throw new ConfigException($"{where} enthält nur „null“ statt einer Konfiguration.");
        }
        catch (JsonException exception)
        {
            throw new ConfigException(Explain(where, exception), exception);
        }
    }

    /// <summary>Schreibt die Konfiguration — nach bestandener Prüfung.</summary>
    /// <remarks>
    /// <b>Erst prüfen, dann schreiben.</b> Eine ungültige Datei zu hinterlassen hiesse, den
    /// nächsten Commit ungebucht zu lassen und den Grund erst beim nächsten Start zu nennen.
    /// </remarks>
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        ConfigValidator.Validate(config, Path);
        AtomicFile.WriteText(Path, JsonSerializer.Serialize(config, ConfigJson.Options) + "\n");
    }

    /// <summary>
    /// Macht aus einer JSON-Fehlermeldung einen Satz, der zur Datei führt.
    /// </summary>
    /// <remarks>
    /// Zeile und Stelle kommen aus der Ausnahme; der Rumpf selbst gehört <b>nicht</b> in die
    /// Meldung — in einer Konfiguration stehen Adressen und Kennungen des Kunden.
    /// </remarks>
    private static string Explain(string where, JsonException exception)
    {
        string position = exception.LineNumber is { } line
            ? string.Create(CultureInfo.InvariantCulture,
                $" (Zeile {line + 1}, Stelle {exception.BytePositionInLine + 1})")
            : string.Empty;

        return $"{where} ist kein gültiges JSON{position}: {exception.Message} "
            + "Unbekannte Felder werden abgelehnt — ein Tippfehler im Namen einer Einstellung "
            + "ist die häufigste Ursache. Die mitgelieferte config.example.json zeigt alle "
            + "gültigen Felder.";
    }
}
