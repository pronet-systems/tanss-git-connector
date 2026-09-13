namespace TanssGitConnector.Storage;

/// <summary>
/// Die festen Ablageorte des Werkzeugs auf dem Rechner des Technikers.
/// </summary>
/// <remarks>
/// <para><b>Zwei Orte, nicht einer.</b> Die Konfiguration ist etwas, das man sichern,
/// versionieren und auf einen zweiten Rechner mitnehmen möchte. Token, Warteschlange und
/// Protokoll gehören genau diesem Rechner und dürfen ihn nicht verlassen — unter Windows liegt
/// der Token DPAPI-versiegelt und ist auf einem anderen Rechner ohnehin nur eine unlesbare
/// Datei.</para>
///
/// <para><b>Die Orte folgen den Gepflogenheiten der jeweiligen Plattform:</b> unter Windows
/// <c>%APPDATA%</c> und <c>%LOCALAPPDATA%</c>, unter Unix die XDG-Verzeichnisse. Unter macOS
/// wäre <c>~/Library/Application Support</c> die Hausordnung des Systems; hier gilt trotzdem
/// XDG, weil dies ein Kommandozeilenwerkzeug ist und weil ein Techniker, der zwischen macOS und
/// Linux wechselt, seine Datei am selben Platz finden soll.</para>
///
/// <para><b>Bewusst kein Verzeichnis je Programmversion.</b> Ein eigenes Verzeichnis je Stand
/// hinterlässt nach vier Versionen vier verwaiste Ordner, von denen keiner erkennbar der
/// gültige ist.</para>
/// </remarks>
public static class StoragePaths
{
    /// <summary>Herstellerordner unter Windows. Teil des Pfads, nicht des Dateinamens.</summary>
    public const string VendorFolder = "ProNet Systems";

    /// <summary>Produktordner unter Windows.</summary>
    public const string ProductFolder = "TanssGitConnector";

    /// <summary>Verzeichnisname unter Unix.</summary>
    public const string UnixFolder = "tanss-git-connector";

    /// <summary>Dateiname der Konfiguration.</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>Dateiname des geschützten Tokens.</summary>
    public const string CredentialsFileName = "credentials.dat";

    /// <summary>Dateiname der Warteschlange.</summary>
    public const string QueueFileName = "queue.json";

    /// <summary>Dateiname des Änderungsprotokolls. Eine Zeile JSON je Vorgang.</summary>
    public const string LogFileName = "log.jsonl";

    /// <summary>Verzeichnisname der Git-Vorlage.</summary>
    public const string TemplateFolderName = "git-template";

    /// <summary>
    /// Umgebungsvariable, die beide Verzeichnisse auf einen eigenen Ort umlenkt.
    /// </summary>
    /// <remarks>
    /// Gedacht für zwei Fälle: einen Probelauf, der das eingerichtete Werkzeug des Technikers
    /// nicht anfassen soll, und Prüfläufe. Ist sie gesetzt, liegen Konfiguration <b>und</b>
    /// Zustand darunter — wer sie setzt, bekommt eine leere Einrichtung und nicht die halbe.
    /// </remarks>
    public const string HomeVariable = "TANSS_GIT_CONNECTOR_HOME";

    /// <summary>Das Verzeichnis der Konfiguration.</summary>
    public static string ConfigDirectory => Override() ?? PlatformConfigDirectory();

    /// <summary>Das Verzeichnis für Token, Warteschlange, Protokoll und Git-Vorlage.</summary>
    public static string StateDirectory => Override() ?? PlatformStateDirectory();

    /// <summary>Vollständiger Pfad der Konfigurationsdatei.</summary>
    public static string ConfigFile => Path.Combine(ConfigDirectory, ConfigFileName);

    /// <summary>Vollständiger Pfad der Token-Datei.</summary>
    public static string CredentialsFile => Path.Combine(StateDirectory, CredentialsFileName);

    /// <summary>Vollständiger Pfad der Warteschlange.</summary>
    public static string QueueFile => Path.Combine(StateDirectory, QueueFileName);

    /// <summary>Vollständiger Pfad des Änderungsprotokolls.</summary>
    public static string LogFile => Path.Combine(StateDirectory, LogFileName);

    /// <summary>
    /// Die Git-Vorlage, die <c>init.templatedir</c> bekommt.
    /// </summary>
    /// <remarks>
    /// Git kopiert beim <c>git init</c> und <c>git clone</c> den <b>Inhalt</b> dieses
    /// Verzeichnisses in das neue Repository. Der Hook liegt deshalb darunter in
    /// <c>hooks/post-commit</c> und nicht unmittelbar hier.
    /// </remarks>
    public static string TemplateDirectory => Path.Combine(StateDirectory, TemplateFolderName);

    /// <summary>Das Hook-Verzeichnis innerhalb der Vorlage.</summary>
    public static string TemplateHooksDirectory => Path.Combine(TemplateDirectory, "hooks");

    private static string? Override()
    {
        string? home = Environment.GetEnvironmentVariable(HomeVariable);
        return string.IsNullOrWhiteSpace(home) ? null : Path.GetFullPath(home);
    }

    private static string PlatformConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                                          Environment.SpecialFolderOption.Create),
                VendorFolder, ProductFolder);
        }

        return Path.Combine(XdgOrDefault("XDG_CONFIG_HOME", ".config"), UnixFolder);
    }

    private static string PlatformStateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                          Environment.SpecialFolderOption.Create),
                VendorFolder, ProductFolder);
        }

        // XDG_STATE_HOME und nicht XDG_DATA_HOME: Warteschlange und Protokoll sind
        // Laufzeitzustand dieses einen Rechners. Wer sein data-Verzeichnis sichert, will
        // Daten sichern und keine halb gesendete Warteschlange zurueckspielen.
        return Path.Combine(XdgOrDefault("XDG_STATE_HOME", Path.Combine(".local", "state")),
                            UnixFolder);
    }

    /// <summary>
    /// Liest eine XDG-Variable, sonst den vorgesehenen Ort im Heimatverzeichnis.
    /// </summary>
    /// <remarks>
    /// Ein relativer Wert in einer XDG-Variablen ist laut Festlegung unwirksam und wird
    /// übergangen — sonst läge die Konfiguration je nach Arbeitsverzeichnis woanders, und das
    /// Werkzeug fände sie beim nächsten Aufruf nicht wieder.
    /// </remarks>
    private static string XdgOrDefault(string variable, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value))
        {
            return value;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, fallback);
    }
}
