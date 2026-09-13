using System.Globalization;

namespace TanssGitConnector.Git;

/// <summary>
/// Ein Commit, so wie dieses Werkzeug ihn braucht.
/// </summary>
/// <remarks>
/// <para>Bewusst nur diese Felder. Was hier steht, landet über den Kommentar in TANSS und damit
/// in der Dokumentation beim Kunden — jedes zusätzliche Feld ist eine Entscheidung darüber,
/// welche Daten das Haus verlassen, und keine Fleissarbeit.</para>
/// <para><b>Keine Dateinamen und keine Änderungen.</b> Ein Dateipfad verrät Kundennamen,
/// Projektnamen und gelegentlich mehr; der Inhalt einer Änderung ist Betriebsgeheimnis des
/// Auftraggebers. Für den Nachweis, dass gearbeitet wurde, genügt die Commit-Meldung, die der
/// Techniker selbst geschrieben hat.</para>
/// </remarks>
public sealed record CommitInfo
{
    /// <summary>Der vollständige Hash. Zugleich die Vorgangskennung gegenüber TANSS.</summary>
    public required string Sha { get; init; }

    /// <summary>Die Hashes der Eltern, durch Leerzeichen getrennt, so wie Git sie ausgibt.</summary>
    public string Parents { get; init; } = string.Empty;

    /// <summary>Wann der Commit entstanden ist — die <b>Committer</b>-Zeit.</summary>
    /// <remarks>
    /// Nicht die Autorenzeit: Bei <c>cherry-pick</c> und <c>rebase</c> ist die Autorenzeit die
    /// des ursprünglichen Schreibens und kann Monate zurückliegen. Gebucht wird, wann die Arbeit
    /// stattgefunden hat, und das ist der Zeitpunkt des Commits auf diesem Rechner.
    /// </remarks>
    public required DateTimeOffset CommittedAt { get; init; }

    /// <summary>Der Name des Autors, so wie er in der Git-Konfiguration steht.</summary>
    public string AuthorName { get; init; } = string.Empty;

    /// <summary>Die E-Mail-Adresse des Autors.</summary>
    public string AuthorEmail { get; init; } = string.Empty;

    /// <summary>Die erste Zeile der Commit-Meldung.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Die vollständige Commit-Meldung einschließlich Betreff.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Der Zweig, auf dem committet wurde; leer bei abgetrenntem HEAD.</summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>Der Name des Repositorys — der letzte Teil des Wurzelverzeichnisses.</summary>
    public string RepositoryName { get; init; } = string.Empty;

    /// <summary>Das Wurzelverzeichnis des Repositorys.</summary>
    public string RepositoryPath { get; init; } = string.Empty;

    /// <summary>Die ersten sieben Zeichen des Hashs — die übliche Kurzform.</summary>
    public string ShortSha => Sha.Length <= 7 ? Sha : Sha[..7];

    /// <summary>
    /// Ist das ein Zusammenführungs-Commit?
    /// </summary>
    /// <remarks>
    /// Zwei Eltern und mehr. Ein solcher Commit trägt selten eigene Arbeitszeit — die steckt in
    /// den Commits, die er zusammenführt, und die sind meist schon gebucht. Ob er übergangen
    /// wird, entscheidet die Konfiguration.
    /// </remarks>
    public bool IsMerge => Parents.Contains(' ', StringComparison.Ordinal);

    /// <summary>
    /// Der Rumpf der Commit-Meldung ohne den Betreff und ohne führende Leerzeilen.
    /// </summary>
    /// <remarks>
    /// Getrennt, weil Betreff und Rumpf in TANSS verschieden wiegen: Der Betreff ist die
    /// Leistungsbeschreibung, der Rumpf die Begründung. Wer den Rumpf nicht mitschicken will,
    /// schaltet ihn ab, ohne die Beschreibung zu verlieren.
    /// </remarks>
    public string Body
    {
        get
        {
            if (Message.Length == 0)
            {
                return string.Empty;
            }

            int firstBreak = Message.IndexOf('\n', StringComparison.Ordinal);
            return firstBreak < 0 ? string.Empty : Message[(firstBreak + 1)..].Trim('\r', '\n', ' ', '\t');
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{ShortSha} {Subject}");
}
