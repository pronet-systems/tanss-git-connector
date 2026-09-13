using System.Text;

namespace TanssGitConnector.Git;

/// <summary>
/// Der Text des <c>post-commit</c>-Hakens.
/// </summary>
/// <remarks>
/// <para><b>Warum ein Shell-Skript und kein Programmaufruf.</b> Git ruft Haken über eine Shell
/// auf — unter Windows über die mitgelieferte Bash von Git für Windows. Ein Skript ist damit die
/// einzige Form, die auf allen drei Plattformen unverändert funktioniert.</para>
///
/// <para><b>Der Haken endet immer mit 0.</b> Wenn er läuft, ist der Commit bereits geschrieben;
/// Git wertet den Rückgabewert von <c>post-commit</c> ohnehin nicht aus. Ein Haken, der einen
/// Fehler nach aussen trägt, sähe nach einem kaputten Commit aus und würde den Techniker zu
/// einem <c>--amend</c> verleiten, das nur einen zweiten Hash erzeugt.</para>
///
/// <para><b>Der Pfad zum Programm steht ausgeschrieben im Haken.</b> Nicht der blosse Name:
/// Läuft Git aus einer Entwicklungsumgebung oder einem Oberflächenprogramm heraus, bringt das
/// häufig einen eigenen, knappen Suchpfad mit, in dem <c>tanss-git</c> nicht liegt. Der Haken
/// liefe dann bei jedem Commit ins Leere, und zwar lautlos.</para>
/// </remarks>
public static class HookScript
{
    /// <summary>Der Dateiname des Hakens.</summary>
    public const string FileName = "post-commit";

    /// <summary>
    /// Die Erkennungsmarke.
    /// </summary>
    /// <remarks>
    /// <b>Sie entscheidet über Anfassen oder Stehenlassen.</b> Ohne sie wird eine vorgefundene
    /// Datei niemals überschrieben und beim Abschalten niemals gelöscht — ein fremder
    /// <c>post-commit</c>-Haken kann ein Prüflauf, eine Signatur oder eine Benachrichtigung
    /// sein, und ihn kommentarlos zu entfernen wäre ein Eingriff in die Arbeit eines anderen.
    /// </remarks>
    public const string Marker = "tanss-git-connector:hook:v1";

    /// <summary>
    /// Baut den Haken für ein bestimmtes Programm.
    /// </summary>
    /// <param name="executablePath">Der vollständige Pfad zu <c>tanss-git</c>.</param>
    /// <returns>Der Skripttext mit Zeilenumbrüchen im Unix-Format.</returns>
    public static string Build(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        string command = ForShell(executablePath);

        // Bewusst Zeile fuer Zeile und mit \n verbunden: Ein Skript mit Wagenruecklaeufen
        // scheitert unter Unix an "bad interpreter: /bin/sh^M" - ein Fehler, der nach einem
        // kaputten Programm aussieht und keiner ist.
        string[] lines =
        [
            "#!/bin/sh",
            "# TANSS Git-Connector — post-commit",
            "#",
            "# Angelegt von „tanss-git enable“. Diese Datei bucht jeden Commit als Fernwartung",
            "# in eurer TANSS-Instanz.",
            "#",
            "# Die folgende Zeile ist die Erkennungsmarke. Ohne sie fasst „tanss-git disable“",
            "# diese Datei nicht an — und „tanss-git enable“ überschreibt sie nicht.",
            "# " + Marker,
            "#",
            "# Der Haken darf den Commit nicht scheitern lassen: Er ist bereits geschrieben,",
            "# wenn diese Zeilen laufen. Deshalb endet die Datei in jedem Fall mit 0.",
            "",
            command + " hook --repository \"$PWD\" || true",
            "exit 0",
            string.Empty,
        ];

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Trägt dieser Text unseren Haken?
    /// </summary>
    /// <param name="content">Der Inhalt der vorgefundenen Datei.</param>
    public static bool IsOurs(string? content) =>
        content is not null && content.Contains(Marker, StringComparison.Ordinal);

    /// <summary>
    /// Bringt einen Pfad in eine Form, die die Shell des Hakens versteht.
    /// </summary>
    /// <remarks>
    /// <para>Zwei Umbauten, beide nötig. Erstens werden umgekehrte Schrägstriche zu
    /// gewöhnlichen: Die Bash von Git für Windows nimmt <c>C:/Programme/…</c> an, liest in
    /// <c>C:\Programme\…</c> die Schrägstriche aber als Maskierungszeichen. Zweitens wird in
    /// einfache Anführungszeichen gesetzt statt in doppelte — ein Pfad kann ein Dollarzeichen
    /// enthalten (<c>%LOCALAPPDATA%</c> nicht, ein selbst gewähltes Verzeichnis durchaus), und
    /// in doppelten Anführungszeichen würde daraus eine Ersetzung.</para>
    /// <para>Ein einfaches Anführungszeichen im Pfad selbst wird nach dem üblichen Muster
    /// beendet, maskiert und wieder geöffnet. Das ist kein Randfall aus Prinzipienreiterei:
    /// Benutzernamen wie <c>O'Brien</c> gibt es, und der Pfad enthält den Benutzernamen.</para>
    /// </remarks>
    /// <param name="path">Der Pfad zum Programm.</param>
    /// <returns>Der Pfad, fertig zum Einsetzen in ein Shell-Skript.</returns>
    public static string ForShell(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string slashes = path.Replace('\\', '/');

        StringBuilder quoted = new(slashes.Length + 2);
        quoted.Append('\'');
        foreach (char character in slashes)
        {
            if (character == '\'')
            {
                quoted.Append("'\\''");
                continue;
            }

            quoted.Append(character);
        }

        quoted.Append('\'');
        return quoted.ToString();
    }
}
