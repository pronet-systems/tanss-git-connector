using System.Globalization;
using System.Text;
using TanssGitConnector.Api;
using TanssGitConnector.Api.Model;

namespace TanssGitConnector.Git;

/// <summary>Woraus sich die gebuchte Dauer eines Commits ergibt.</summary>
public enum DurationMode
{
    /// <summary>
    /// Jeder Commit wird mit derselben Dauer gebucht.
    /// </summary>
    /// <remarks>
    /// Die Voreinstellung, weil sie ehrlich ist: Sie behauptet nichts über die Arbeitszeit,
    /// sondern setzt einen vereinbarten Pauschalwert. Wer sie benutzt, weiß, dass die Summe am
    /// Monatsende eine Pauschale ist und keine Messung.
    /// </remarks>
    Fixed,

    /// <summary>
    /// Die Zeit seit dem vorigen Commit, begrenzt nach oben und unten.
    /// </summary>
    /// <remarks>
    /// <para>Näher an der Wirklichkeit, solange in einem Rutsch gearbeitet wird — und deutlich
    /// daneben, sobald jemand einen Tag Pause macht oder die Zwischenzeit anderweitig gebucht
    /// ist. Genau dagegen steht die Obergrenze.</para>
    /// <para><b>Auch das bleibt eine Schätzung.</b> Git weiß nicht, wann jemand angefangen hat
    /// zu arbeiten; es weiß nur, wann er zuletzt committet hat. Wer eine Messung braucht,
    /// benutzt die Timer in TANSS.</para>
    /// </remarks>
    SinceLastCommit,
}

/// <summary>Alles, was aus einem Commit eine Fernwartung macht.</summary>
public sealed record BookingSettings
{
    /// <summary>Die externe Fernwartungs-Anbindung in TANSS. Muss mindestens 1000 sein.</summary>
    public required int RemoteSupportTypeId { get; init; }

    /// <summary>Der Techniker, auf den gebucht wird.</summary>
    public required int EmployeeId { get; init; }

    /// <summary>Wie die Dauer zustande kommt.</summary>
    public DurationMode Mode { get; init; } = DurationMode.Fixed;

    /// <summary>Die feste Dauer — und zugleich der Rückfall, wenn sich keine Zeit bemessen lässt.</summary>
    public TimeSpan Fixed { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Untergrenze im Modus <see cref="DurationMode.SinceLastCommit"/>.</summary>
    public TimeSpan Minimum { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Obergrenze im Modus <see cref="DurationMode.SinceLastCommit"/>.</summary>
    public TimeSpan Maximum { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Soll der Rumpf der Commit-Meldung mit nach TANSS?</summary>
    public bool IncludeBody { get; init; } = true;

    /// <summary>Soll der Zweigname mit nach TANSS?</summary>
    public bool IncludeBranch { get; init; } = true;

    /// <summary>Soll der Name des Repositorys mit nach TANSS?</summary>
    public bool IncludeRepository { get; init; } = true;
}

/// <summary>
/// Macht aus einem Commit eine Fernwartung.
/// </summary>
/// <remarks>
/// <para><b>Vollständig ohne Netz und ohne Dateisystem.</b> Diese Klasse ist die einzige Stelle,
/// an der entschieden wird, welche Angaben eines Commits das Haus verlassen und mit welcher
/// Dauer gebucht wird. Beides gehört geprüft, und zwar ohne TANSS-Instanz und ohne
/// Repository.</para>
/// <para><b>Die Zeitrechnung läuft rückwärts vom Commit.</b> Gearbeitet wurde, bevor committet
/// wurde — der Commit ist das Ende der Arbeit und nicht ihr Anfang. Eine Fernwartung, die beim
/// Commit beginnt, läge komplett in der Zukunft.</para>
/// </remarks>
public static class CommitBooking
{
    /// <summary>
    /// Grenze für den Kommentar in Zeichen.
    /// </summary>
    /// <remarks>
    /// <b>Nicht gemessen, sondern vorsorglich.</b> Welche Länge TANSS in <c>comment</c>
    /// entgegennimmt, ist nicht belegt. Ein Commit-Rumpf kann Dutzende Kilobyte tragen — etwa
    /// ein eingefügtes Protokoll —, und der Ausgang wäre entweder eine abgewiesene Anfrage oder
    /// ein serverseitig stillschweigend abgeschnittener Text. Lieber hier kürzen und es
    /// dazuschreiben.
    /// </remarks>
    public const int MaxCommentLength = 4000;

    /// <summary>Der Hinweis, der an einen gekürzten Kommentar tritt.</summary>
    public const string TruncationNotice = "… [gekürzt]";

    /// <summary>
    /// Die Dauer, mit der dieser Commit gebucht wird.
    /// </summary>
    /// <param name="commit">Der Commit.</param>
    /// <param name="previousCommit">
    /// Wann der Commit davor entstand, oder <see langword="null"/> beim Wurzelcommit.
    /// </param>
    /// <param name="settings">Die Einstellungen.</param>
    /// <returns>Eine Dauer grösser als Null.</returns>
    public static TimeSpan DurationFor(CommitInfo commit, DateTimeOffset? previousCommit,
                                       BookingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Mode == DurationMode.Fixed || previousCommit is not { } previous)
        {
            return Positive(settings.Fixed);
        }

        TimeSpan elapsed = commit.CommittedAt - previous;

        // Eine nicht positive Spanne heisst: die Uhr ist gesprungen, oder die Reihenfolge der
        // Commits entspricht nicht ihrer Zeitfolge (nach einem rebase der Normalfall). Dann ist
        // die Vorgabe der ehrlichere Wert als eine gerechnete Null.
        if (elapsed <= TimeSpan.Zero)
        {
            return Positive(settings.Fixed);
        }

        TimeSpan lower = Positive(settings.Minimum);
        TimeSpan upper = settings.Maximum > lower ? settings.Maximum : lower;

        return elapsed < lower ? lower : elapsed > upper ? upper : elapsed;
    }

    /// <summary>
    /// Baut den Kommentar, der in TANSS an der Fernwartung steht.
    /// </summary>
    /// <remarks>
    /// <para>Aufbau: Betreff, dann der Rumpf, dann eine Herkunftszeile. Der Betreff steht oben,
    /// weil TANSS ihn in Listen abschneidet — und was abgeschnitten wird, soll die
    /// Herkunftszeile sein und nicht die Leistungsbeschreibung.</para>
    /// <para><b>Ein Commit ohne Betreff ist möglich</b> (<c>git commit --allow-empty-message</c>)
    /// und ergäbe einen leeren Kommentar. Dann steht dort ein Satz, der genau das sagt, statt
    /// einer leeren Zeile, die wie ein Fehler des Werkzeugs aussieht.</para>
    /// </remarks>
    /// <param name="commit">Der Commit.</param>
    /// <param name="settings">Die Einstellungen.</param>
    /// <returns>Der Kommentar, höchstens <see cref="MaxCommentLength"/> Zeichen lang.</returns>
    public static string Comment(CommitInfo commit, BookingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(settings);

        StringBuilder text = new();

        string subject = commit.Subject.Trim();
        text.Append(subject.Length > 0 ? subject : "Commit ohne Meldung");

        if (settings.IncludeBody && commit.Body is { Length: > 0 } body)
        {
            text.Append("\n\n").Append(body);
        }

        text.Append("\n\n").Append(Origin(commit, settings));

        return Shorten(text.ToString());
    }

    /// <summary>
    /// Die Herkunftszeile: Commit, Repository, Zweig.
    /// </summary>
    /// <remarks>
    /// Der Kurzhash steht hier für Menschen. Der vollständige Hash steht in
    /// <c>remoteMaintenanceId</c> und ist die Grundlage der Existenzprüfung — beides hat einen
    /// eigenen Platz, und keines ersetzt das andere.
    /// </remarks>
    public static string Origin(CommitInfo commit, BookingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(settings);

        List<string> parts = ["Commit " + commit.ShortSha];

        if (settings.IncludeRepository && commit.RepositoryName is { Length: > 0 } repository)
        {
            parts.Add("Repository " + repository);
        }

        if (settings.IncludeBranch && commit.Branch is { Length: > 0 } branch)
        {
            parts.Add("Zweig " + branch);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Baut die Fernwartung, die nach TANSS geht.
    /// </summary>
    /// <param name="commit">Der Commit.</param>
    /// <param name="previousCommit">Wann der Commit davor entstand; <see langword="null"/> beim Wurzelcommit.</param>
    /// <param name="ticketId">Die Ticketnummer, oder 0 für keinen Ticketbezug.</param>
    /// <param name="settings">Die Einstellungen.</param>
    public static RemoteSupportWrite Build(CommitInfo commit, DateTimeOffset? previousCommit,
                                           int ticketId, BookingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(settings);

        TimeSpan duration = DurationFor(commit, previousCommit, settings);

        return new RemoteSupportWrite
        {
            TypeId = settings.RemoteSupportTypeId,
            EmployeeId = settings.EmployeeId,
            StartTime = TanssTime.ToUnixSeconds(commit.CommittedAt - duration),
            EndTime = TanssTime.ToUnixSeconds(commit.CommittedAt),

            // Der volle Hash, nicht die Kurzform: Die Kurzform ist nur innerhalb eines
            // Repositorys eindeutig, und in TANSS treffen die Commits aller Repositorys
            // aufeinander. Eine Kollision hiesse, dass die Existenzpruefung einen fremden
            // Commit fuer den eigenen haelt und die Buchung ausbleibt.
            RemoteMaintenanceId = commit.Sha,
            Comment = Comment(commit, settings),
            TicketId = ticketId > 0 ? ticketId : 0,
            DeviceName = settings.IncludeRepository ? commit.RepositoryName : string.Empty,
        };
    }

    /// <summary>Kürzt einen Text auf <see cref="MaxCommentLength"/> und sagt, dass gekürzt wurde.</summary>
    public static string Shorten(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= MaxCommentLength)
        {
            return text;
        }

        int room = MaxCommentLength - TruncationNotice.Length;
        return string.Create(CultureInfo.InvariantCulture,
            $"{text.AsSpan(0, room).TrimEnd()}{TruncationNotice}");
    }

    private static TimeSpan Positive(TimeSpan value) =>
        value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(1);
}
