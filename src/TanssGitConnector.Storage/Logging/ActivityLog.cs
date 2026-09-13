using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TanssGitConnector.Api.Diagnostics;

namespace TanssGitConnector.Storage.Logging;

/// <summary>Eine Zeile des Änderungsprotokolls.</summary>
/// <remarks>
/// Eine Zeile JSON je Vorgang. Das Format ist mit Absicht zeilenweise: Es lässt sich anhängen,
/// ohne die Datei zu lesen, es übersteht eine abgeschnittene letzte Zeile, und es ist mit
/// gewöhnlichen Werkzeugen zu durchsuchen.
/// </remarks>
public sealed record LogEntry
{
    /// <summary>Wann.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary><c>debug</c>, <c>info</c>, <c>warning</c> oder <c>error</c>.</summary>
    public required string Level { get; init; }

    /// <summary>Der Anlass, etwa <c>hook.enqueue</c> oder <c>queue.sent</c>.</summary>
    public required string Event { get; init; }

    /// <summary>Was geschehen ist, in einem Satz.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Der Commit, um den es ging — die Kurzform genügt zum Wiederfinden.</summary>
    public string? Commit { get; init; }

    /// <summary>Das Repository.</summary>
    public string? Repository { get; init; }

    /// <summary>Das Ticket, sofern eines gesetzt wurde.</summary>
    public int? TicketId { get; init; }
}

/// <summary>
/// Das Änderungsprotokoll auf dem Rechner des Technikers.
/// </summary>
/// <remarks>
/// <para>Es hält fest <b>warum</b>, nicht nur dass: übergangene Commits mit Grund, Buchungen mit
/// Ticket, Fehlschläge mit Meldung. Wer am Monatsende eine Lücke sucht, findet hier den Grund —
/// in TANSS ist bei einem übergangenen Commit nie etwas angekommen, es gibt also sonst nirgends
/// einen Beleg.</para>
///
/// <para><b>Geheimnisse werden niemals protokolliert.</b> Jede Meldung läuft durch
/// <see cref="Redaction"/>; JWT-Muster und <c>Bearer</c>-Werte verschwinden dabei. Das gilt
/// immer und lässt sich nicht abschalten.</para>
///
/// <para><b>Ein Fehler beim Protokollieren darf nichts kosten.</b> Kann nicht geschrieben
/// werden — volle Platte, gesperrte Datei —, geschieht nichts weiter. Ein Commit, der nicht
/// gebucht wird, weil das Protokoll klemmt, wäre die Umkehrung aller Verhältnisse.</para>
/// </remarks>
public sealed class ActivityLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly TimeProvider _time;

    /// <summary>Öffnet das Protokoll an einem beliebigen Pfad.</summary>
    public ActivityLog(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Öffnet das Protokoll am vorgesehenen Ort.</summary>
    public static ActivityLog Default(TimeProvider? time = null) => new(StoragePaths.LogFile, time);

    /// <summary>Der Pfad der Datei.</summary>
    public string Path => _path;

    /// <summary>Schreibt eine Zeile.</summary>
    /// <param name="level"><c>debug</c>, <c>info</c>, <c>warning</c> oder <c>error</c>.</param>
    /// <param name="eventName">Der Anlass, etwa <c>queue.sent</c>.</param>
    /// <param name="message">Was geschehen ist.</param>
    /// <param name="commit">Der Commit-Hash, sofern einer im Spiel ist.</param>
    /// <param name="repository">Das Repository.</param>
    /// <param name="ticketId">Das Ticket, sofern eines gesetzt wurde.</param>
    public void Write(string level, string eventName, string message, string? commit = null,
                      string? repository = null, int? ticketId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        LogEntry entry = new()
        {
            At = _time.GetUtcNow(),
            Level = level,
            Event = eventName,
            Message = Redaction.Scrub(message),
            Commit = Shorten(commit),
            Repository = repository,
            TicketId = ticketId is > 0 ? ticketId : null,
        };

        Append(JsonSerializer.Serialize(entry, Json));
    }

    /// <summary>Schreibt eine Zeile der Stufe <c>info</c>.</summary>
    public void Info(string eventName, string message, string? commit = null,
                     string? repository = null, int? ticketId = null) =>
        Write("info", eventName, message, commit, repository, ticketId);

    /// <summary>Schreibt eine Zeile der Stufe <c>warning</c>.</summary>
    public void Warning(string eventName, string message, string? commit = null,
                        string? repository = null, int? ticketId = null) =>
        Write("warning", eventName, message, commit, repository, ticketId);

    /// <summary>Schreibt eine Zeile der Stufe <c>error</c>.</summary>
    public void Error(string eventName, string message, string? commit = null,
                      string? repository = null, int? ticketId = null) =>
        Write("error", eventName, message, commit, repository, ticketId);

    /// <summary>Liest die jüngsten Zeilen, jüngste zuerst.</summary>
    /// <remarks>
    /// Eine unlesbare Zeile wird übergangen statt zu werfen: Ein abgeschnittener letzter Eintrag
    /// nach einem Stromausfall darf nicht das ganze Protokoll unlesbar machen.
    /// </remarks>
    /// <param name="count">Wie viele Zeilen höchstens.</param>
    public IReadOnlyList<LogEntry> Tail(int count = 50)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        if (!File.Exists(_path))
        {
            return [];
        }

        List<LogEntry> entries = [];

        foreach (string line in ReadLines().Reverse())
        {
            if (Parse(line) is { } entry)
            {
                entries.Add(entry);
            }

            if (entries.Count >= count)
            {
                break;
            }
        }

        return entries;
    }

    /// <summary>
    /// Nimmt Zeilen fort, die älter sind als die Frist.
    /// </summary>
    /// <remarks>
    /// Die Datei wird dabei neu geschrieben — atomar, wie alles hier. Eine unlesbare Zeile
    /// bleibt <b>stehen</b>: Was sich nicht lesen lässt, lässt sich auch nicht datieren, und
    /// eine Zeile fortzunehmen, deren Alter niemand kennt, hiesse raten.
    /// </remarks>
    /// <param name="retention">Die Frist. Null oder negativ räumt alles fort.</param>
    /// <returns>Wie viele Zeilen fortgenommen wurden.</returns>
    public int Prune(TimeSpan retention)
    {
        if (!File.Exists(_path))
        {
            return 0;
        }

        DateTimeOffset limit = _time.GetUtcNow() - (retention > TimeSpan.Zero ? retention : TimeSpan.Zero);

        List<string> kept = [];
        int removed = 0;

        foreach (string line in ReadLines())
        {
            LogEntry? entry = Parse(line);

            if (entry is not null && entry.At <= limit)
            {
                removed++;
                continue;
            }

            kept.Add(line);
        }

        if (removed > 0)
        {
            AtomicFile.WriteText(_path, kept.Count == 0 ? string.Empty
                : string.Join('\n', kept) + "\n");
        }

        return removed;
    }

    private IEnumerable<string> ReadLines()
    {
        string[] lines;

        try
        {
            lines = File.ReadAllLines(_path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ein unlesbares Protokoll ist ein Aergernis und kein Grund, einen Befehl
            // abzubrechen - es traegt keine Arbeitszeit, sondern deren Begruendung.
            return [];
        }

        return lines.Where(static line => !string.IsNullOrWhiteSpace(line));
    }

    private static LogEntry? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<LogEntry>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Append(string line)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

            // Anhaengen statt lesen-aendern-schreiben: Zwei Haken gleichzeitig schreiben dann
            // zwei Zeilen und nicht eine halbe. FileShare.ReadWrite, damit ein zweiter Aufruf
            // nicht an der offenen Datei scheitert.
            using FileStream stream = new(_path, FileMode.Append, FileAccess.Write,
                                          FileShare.ReadWrite);
            using StreamWriter writer = new(stream, new UTF8Encoding(false));
            writer.Write(line);
            writer.Write('\n');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Absicht: Das Protokoll ist die Begruendung, nicht der Vorgang. Wer hier wuerfe,
            // liesse eine volle Platte einen Commit kosten.
        }
    }

    private static string? Shorten(string? commit) => commit is { Length: > 7 }
        ? commit[..7]
        : commit;
}
