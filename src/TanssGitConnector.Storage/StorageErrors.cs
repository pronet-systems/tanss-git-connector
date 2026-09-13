namespace TanssGitConnector.Storage;

/// <summary>Basis aller Fehler, die aus der Ablage auf dem Rechner stammen.</summary>
/// <remarks>
/// Bewusst getrennt von <c>TanssException</c>: Ein Fehler hier bedeutet <b>nicht</b>, dass
/// TANSS unerreichbar wäre, sondern dass auf diesem Rechner etwas fehlt oder unlesbar ist.
/// Der Aufrufer darf darauf nicht mit einem Wiederholungsversuch gegen das Netz reagieren.
/// </remarks>
public class StorageException : Exception
{
    /// <summary>Erzeugt einen Ablagefehler.</summary>
    public StorageException() { }

    /// <summary>Erzeugt einen Ablagefehler mit Meldung.</summary>
    public StorageException(string message) : base(message) { }

    /// <summary>Erzeugt einen Ablagefehler mit Meldung und Ursache.</summary>
    public StorageException(string message, Exception? innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Die Konfiguration fehlt, ist kein gültiges JSON oder verletzt eine Regel.
/// </summary>
/// <remarks>
/// Der Aufrufer soll die Meldung <b>wörtlich ausgeben</b> und abbrechen. Sie nennt bereits
/// Datei, Stelle und den Befehl, der das Problem behebt. Ein Weiterlaufen mit Standardwerten
/// wäre falsch: Eine unbrauchbare Konfiguration bedeutet, dass niemand weiß, wohin
/// geschrieben werden soll.
/// </remarks>
public class ConfigException : StorageException
{
    /// <summary>Erzeugt einen Konfigurationsfehler.</summary>
    public ConfigException() { }

    /// <summary>Erzeugt einen Konfigurationsfehler mit Meldung.</summary>
    public ConfigException(string message) : base(message) { }

    /// <summary>Erzeugt einen Konfigurationsfehler mit Meldung und Ursache.</summary>
    public ConfigException(string message, Exception? innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Die Konfiguration ist syntaktisch in Ordnung, verletzt aber eine oder mehrere Regeln.
/// </summary>
/// <remarks>
/// Trägt <see cref="Problems"/> als Einzelmeldungen, damit eine Oberfläche sie an das
/// jeweilige Eingabefeld heften kann. <see cref="Exception.Message"/> enthält dieselben
/// Angaben bereits fertig untereinander gesetzt — es gibt also nichts nachzuformatieren.
/// <b>Alle</b> Verstöße stehen darin, nicht nur der erste: Wer eine Konfiguration von Hand
/// pflegt, soll sie in einem Durchgang richtigstellen können.
/// </remarks>
public sealed class ConfigValidationException : ConfigException
{
    /// <summary>Die Einzelverstöße, je Eintrag <c>pfad: was stimmt nicht</c>.</summary>
    public IReadOnlyList<string> Problems { get; } = [];

    /// <summary>Erzeugt einen leeren Prüffehler.</summary>
    public ConfigValidationException() { }

    /// <summary>Erzeugt einen Prüffehler mit Meldung.</summary>
    public ConfigValidationException(string message) : base(message) { }

    /// <summary>Erzeugt einen Prüffehler mit Meldung und Ursache.</summary>
    public ConfigValidationException(string message, Exception? innerException)
        : base(message, innerException) { }

    /// <summary>Erzeugt einen Prüffehler aus einer Liste von Einzelverstößen.</summary>
    public ConfigValidationException(string headline, IReadOnlyList<string> problems)
        : base(Compose(headline, problems)) => Problems = problems;

    private static string Compose(string headline, IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return string.Join(Environment.NewLine,
            [headline, .. problems.Select(static p => "  " + p)]);
    }
}

/// <summary>
/// Das Arbeitstoken fehlt oder lässt sich nicht entschlüsseln.
/// </summary>
/// <remarks>
/// Zwei getrennte Ursachen, die beide hier landen und in der Meldung unterschieden werden:
/// <b>Es ist nie eines hinterlegt worden</b> — dann nennt die Meldung den Einrichtungsbefehl.
/// Oder <b>die Datei gehört einem anderen Benutzerkonto</b> — DPAPI
/// entschlüsselt dann grundsätzlich nicht, und kein Wiederholungsversuch ändert daran etwas.
/// Der Aufrufer darf in beiden Fällen nicht erneut anmelden, sondern muss den Benutzer
/// zur Einrichtung schicken.
/// </remarks>
public sealed class TokenStoreException : StorageException
{
    /// <summary>Erzeugt einen Token-Fehler.</summary>
    public TokenStoreException() { }

    /// <summary>Erzeugt einen Token-Fehler mit Meldung.</summary>
    public TokenStoreException(string message) : base(message) { }

    /// <summary>Erzeugt einen Token-Fehler mit Meldung und Ursache.</summary>
    public TokenStoreException(string message, Exception? innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Die Warteschlange auf der Platte ist unbrauchbar: beschädigt, gesperrt oder aus einer
/// neueren Programmversion.
/// </summary>
/// <remarks>
/// Eine Warteschlange mit höherer Versionsnummer wird <b>nicht</b> gelesen. Ein älterer
/// Programmstand kennt die neueren Felder nicht und schriebe sie beim nächsten Speichern
/// stillschweigend fort — wartende Commits wären danach unvollständig, und unvollständig
/// heisst hier: nicht gebucht und nicht mehr auffindbar.
/// </remarks>
public sealed class QueueException : StorageException
{
    /// <summary>Erzeugt einen Warteschlangenfehler.</summary>
    public QueueException() { }

    /// <summary>Erzeugt einen Warteschlangenfehler mit Meldung.</summary>
    public QueueException(string message) : base(message) { }

    /// <summary>Erzeugt einen Warteschlangenfehler mit Meldung und Ursache.</summary>
    public QueueException(string message, Exception? innerException)
        : base(message, innerException) { }
}
