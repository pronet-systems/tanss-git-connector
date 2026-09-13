using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using TanssGitConnector.Api.Contract;

namespace TanssGitConnector.Storage.Secrets;

/// <summary>
/// Das Gemeinsame aller Tokenspeicher: das Präfix, die Sicherung, die Meldungen.
/// </summary>
/// <remarks>
/// <b>Das Token ist ein Ausweis.</b> Es erlaubt, im Namen des Technikers in TANSS zu handeln,
/// und es läuft Monate. Wie gut es auf der Platte geschützt ist, entscheidet die Plattform —
/// was in jedem Fall gilt, steht hier.
/// </remarks>
public static class TokenProtection
{
    /// <summary>Das Präfix, das der Kopfzeilenwert <c>apiToken</c> tragen muss.</summary>
    public const string BearerPrefix = "Bearer ";

    /// <summary>Endung der Sicherungsdatei.</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>
    /// Stellt das Präfix <c>Bearer </c> sicher.
    /// </summary>
    /// <remarks>
    /// TANSS liefert unter <c>/api/v1/jwts/tanss_app</c> ein <c>apiToken</c>, das das Präfix
    /// bereits trägt; die Anmeldung unter <c>/api/v1/login</c> liefert unter <c>apiKey</c>
    /// dagegen den nackten Wert. Beides landet hier. Ohne Präfix antwortet TANSS auf jede
    /// Anfrage mit 403 — ununterscheidbar von einem abgelaufenen Token, und deshalb ein Fehler,
    /// der stundenlang in die falsche Richtung führt. Die Vereinheitlichung geschieht an dieser
    /// einen Stelle, weil sie sonst an jeder Aufrufstelle vergessen werden kann.
    /// </remarks>
    public static string Normalize(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        string trimmed = token.Trim();
        return trimmed.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? BearerPrefix + trimmed[BearerPrefix.Length..].Trim()
            : BearerPrefix + trimmed;
    }

    /// <summary>Der Speicher, der auf dieser Plattform vorgesehen ist.</summary>
    /// <remarks>
    /// <para>Unter Windows DPAPI, sonst eine Datei, die nur dem Benutzer gehört. Der Unterschied
    /// ist kein Versehen, sondern die ehrliche Abbildung dessen, was die Plattformen ohne
    /// weitere Abhängigkeiten hergeben — und <c>tanss-git doctor</c> sagt ausdrücklich, welcher
    /// Schutz gerade gilt.</para>
    /// <para><b>Keine eigene Verschlüsselung unter Unix.</b> Ein Schlüssel, der neben dem
    /// Geheimtext liegt, schützt vor niemandem und sieht nur so aus. Wer mehr braucht, legt das
    /// Zustandsverzeichnis auf ein verschlüsseltes Dateisystem.</para>
    /// </remarks>
    /// <param name="path">Der Pfad der Datei; ohne Angabe der vorgesehene Ort.</param>
    public static ITokenStore Default(string? path = null)
    {
        string file = path ?? StoragePaths.CredentialsFile;

        return OperatingSystem.IsWindows()
            ? new DpapiTokenStore(file)
            : new FileTokenStore(file);
    }

    /// <summary>Der Satz, der im Doktor beschreibt, wie das Token geschützt ist.</summary>
    public static string DescribeProtection() => OperatingSystem.IsWindows()
        ? "DPAPI, an das Windows-Konto dieses Benutzers gebunden"
        : "Dateirechte 0600 — nur dieser Benutzer darf lesen; verschlüsselt ist sie nicht";
}

/// <summary>
/// Das TANSS-Arbeitstoken, DPAPI-verschlüsselt.
/// </summary>
/// <remarks>
/// <para>Verschlüsselt wird mit <see cref="DataProtectionScope.CurrentUser"/>. Der Schlüssel
/// hängt damit am Windows-Anmeldekonto: Ein anderer Benutzer desselben Rechners kann die Datei
/// lesen, aber nicht entschlüsseln.</para>
///
/// <para><b>Warum eine feste zusätzliche Entropie.</b> Ohne sie genügt es, die Datei zu lesen
/// und <c>Unprotect</c> aufzurufen — jedes beliebige Programm im selben Benutzerkontext kann
/// das, ganz ohne erhöhte Rechte. Die Entropie zwingt dazu, auch diesen Wert zu kennen. Sie
/// steht bewusst als Konstante im Quelltext und wird nicht zufällig erzeugt: Ein zufälliger
/// Wert müsste neben dem Geheimtext liegen, wo ihn genau derselbe Angreifer fände. Sie ist kein
/// Kennwort und ersetzt DPAPI nicht — sie hebt die Hürde von „Datei lesen“ auf „diesen
/// Programmstand kennen“.</para>
///
/// <para><b>Vor jedem Schreiben entsteht eine <c>.bak</c>.</b> Der gefährliche Moment ist die
/// Token-Erneuerung: Wäre der neue Wert unbrauchbar und der alte bereits überschrieben, hätte
/// der Techniker gar kein Token mehr.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore : ITokenStore
{
    /// <summary>
    /// Zusätzliche Entropie. Ändert sich dieser Wert, sind alle bisherigen Dateien unlesbar —
    /// er ist Teil des Dateiformats und wird nur mit einer Überführung angefasst.
    /// </summary>
    private static readonly byte[] Entropy =
        "ProNet Systems/TanssGitConnector/credentials/v1"u8.ToArray();

    /// <summary>Legt den Speicher an einem beliebigen Pfad an.</summary>
    public DpapiTokenStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        BackupPath = Path + TokenProtection.BackupSuffix;
    }

    /// <summary>Pfad der verschlüsselten Datei.</summary>
    public string Path { get; }

    /// <summary>Pfad der Sicherung des vorherigen Standes.</summary>
    public string BackupPath { get; }

    /// <summary>Liegt bereits ein Token?</summary>
    public bool Exists() => File.Exists(Path);

    /// <inheritdoc/>
    public string Read()
    {
        byte[] cipher = ReadFile(Path);

        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new TokenStoreException(
                $"{Path} liess sich nicht entschlüsseln. DPAPI bindet das Token an das "
                + "Windows-Konto, unter dem es abgelegt wurde: Eine Datei aus einem anderen "
                + "Profil oder von einem anderen Rechner lässt sich hier grundsätzlich nicht "
                + "öffnen, und kein weiterer Versuch ändert daran etwas. Neu einrichten mit: "
                + $"tanss-git setup. Ursprüngliche Meldung: {exception.Message}", exception);
        }

        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <inheritdoc/>
    public void Write(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        byte[] plain = Encoding.UTF8.GetBytes(TokenProtection.Normalize(token));

        byte[] cipher;
        try
        {
            cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new TokenStoreException(
                "Windows konnte das Token nicht verschlüsseln. Das deutet auf ein beschädigtes "
                + "Benutzerprofil hin; eine Neuanmeldung in Windows stellt den Schlüsselspeicher "
                + $"meist wieder her. Ursprüngliche Meldung: {exception.Message}", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        // Erst sichern, dann ueberschreiben. Andersherum waere der Zeitraum, in dem gar kein
        // brauchbares Token existiert, genau der Zeitraum des Schreibens.
        AtomicFile.Backup(Path, BackupPath);
        AtomicFile.WriteBytes(Path, cipher);
    }

    /// <summary>Entfernt Token und Sicherung. Für die Abmeldung.</summary>
    public void Clear()
    {
        TokenFiles.TryDelete(Path);
        TokenFiles.TryDelete(BackupPath);
    }

    private static byte[] ReadFile(string path) => TokenFiles.Read(path);
}

/// <summary>
/// Das TANSS-Arbeitstoken in einer Datei, die nur dem Benutzer gehört.
/// </summary>
/// <remarks>
/// <para>Der Weg ausserhalb von Windows. Die Datei wird mit den Rechten 0600 angelegt: Nur der
/// Eigentümer darf lesen und schreiben, sonst niemand.</para>
///
/// <para><b>Und das ist ausdrücklich weniger als DPAPI.</b> Der Inhalt steht im Klartext auf
/// der Platte; wer die Datei in die Hand bekommt — als <c>root</c>, über eine Sicherung, über
/// eine ausgebaute Platte —, hat das Token. Eine Verschlüsselung mit einem Schlüssel, der
/// daneben liegt, wäre Theater und kein Schutz; die ehrliche Antwort ist ein verschlüsseltes
/// Dateisystem und eine kurze Laufzeit des Tokens. <c>tanss-git doctor</c> sagt das auch so.</para>
///
/// <para>Ein Schlüsselbund des Systems (Secret Service, Keychain) wäre der nächste Schritt. Er
/// ist hier bewusst nicht eingebaut: Auf einem Server ohne angemeldete Sitzung gibt es keinen,
/// und ein Werkzeug, das dann gar nicht erst läuft, hilft niemandem.</para>
/// </remarks>
public sealed class FileTokenStore : ITokenStore
{
    /// <summary>Legt den Speicher an einem beliebigen Pfad an.</summary>
    public FileTokenStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        BackupPath = Path + TokenProtection.BackupSuffix;
    }

    /// <summary>Pfad der Datei.</summary>
    public string Path { get; }

    /// <summary>Pfad der Sicherung des vorherigen Standes.</summary>
    public string BackupPath { get; }

    /// <summary>Liegt bereits ein Token?</summary>
    public bool Exists() => File.Exists(Path);

    /// <inheritdoc/>
    public string Read() => Encoding.UTF8.GetString(TokenFiles.Read(Path)).Trim();

    /// <summary>Lesen und Schreiben nur für den Eigentümer — die Rechte 0600.</summary>
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <inheritdoc/>
    public void Write(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Die Rechte gehen an beide Dateien und werden vor dem Umbenennen gesetzt: Eine
        // Sicherung mit Vorgaberechten gaebe genau das Geheimnis preis, das die Hauptdatei
        // schuetzt.
        AtomicFile.Backup(Path, BackupPath, OwnerOnly);
        AtomicFile.WriteBytes(Path, Encoding.UTF8.GetBytes(TokenProtection.Normalize(token)),
                              OwnerOnly);
    }

    /// <summary>Entfernt Token und Sicherung. Für die Abmeldung.</summary>
    public void Clear()
    {
        TokenFiles.TryDelete(Path);
        TokenFiles.TryDelete(BackupPath);
    }
}

/// <summary>Das Dateihandwerk, das sich beide Speicher teilen.</summary>
internal static class TokenFiles
{
    /// <summary>Liest die Datei und macht aus jedem Fehlschlag einen Satz mit Anleitung.</summary>
    public static byte[] Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new TokenStoreException(
                $"Es ist noch kein TANSS-Token hinterlegt (erwartet unter {path}). Das Werkzeug "
                + "ist auf diesem Rechner noch nicht eingerichtet. Bitte einmalig ausführen: "
                + "tanss-git setup");
        }

        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new TokenStoreException(
                $"{path} liess sich nicht lesen: {exception.Message}", exception);
        }

        return content.Length > 0
            ? content
            : throw new TokenStoreException(
                $"{path} ist leer. Das Token ist verloren. Eine Sicherung läge unter "
                + $"{path + TokenProtection.BackupSuffix}; andernfalls neu einrichten mit: "
                + "tanss-git setup");
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Ein liegengebliebenes Token ist unschoen; den eigentlichen Fehler verdecken
            // darf der Aufraeumversuch nicht.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
