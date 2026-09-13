using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TanssGitConnector.Api.Model;

namespace TanssGitConnector.Storage.Queue;

/// <summary>Was das Einreihen ergeben hat.</summary>
/// <param name="Added">Ist der Commit neu eingereiht worden?</param>
/// <param name="Existing">Der bereits vorhandene Eintrag, sonst <see langword="null"/>.</param>
public sealed record EnqueueResult(bool Added, QueuedCommit? Existing);

/// <summary>
/// Die Warteschlange der Commits, die nach TANSS sollen.
/// </summary>
/// <remarks>
/// <para><b>Der Commit wird eingereiht, bevor irgendetwas ins Netz geht.</b> Das ist die ganze
/// Verlustsicherheit dieses Werkzeugs: Ein Netzausfall, ein zugeklappter Rechner oder ein
/// abgebrochener Haken kostet dann keine Arbeitszeit, sondern verschiebt sie nur.</para>
///
/// <para><b>Der Commit-Hash ist der Schlüssel.</b> Ein zweites Einreihen desselben Commits —
/// nach einem <c>tanss-git book HEAD</c> von Hand, nach einem zweiten Haken, nach einem
/// wiederholten Lauf — wird abgelehnt, nicht angehängt. Der Eintrag bleibt auch nach dem Senden
/// stehen, bis die Frist ihn fortnimmt; ohne ihn wäre die Sperre nach dem ersten erfolgreichen
/// Senden wieder offen.</para>
///
/// <para><b>Jede Änderung läuft unter einer Dateisperre.</b> Zwei Commits in zwei Repositorys
/// im selben Augenblick sind kein Sonderfall, sondern Alltag, sobald jemand zwei Fenster offen
/// hat. Ohne Sperre gewönne der letzte Schreiber, und der Commit des anderen wäre fort.</para>
/// </remarks>
public sealed class CommitOutbox
{
    /// <summary>
    /// Nach so vielen misslungenen Versuchen wird aufgegeben.
    /// </summary>
    /// <remarks>
    /// Aufgeben heißt hier <b>nicht</b> löschen: Die Zeile bleibt als <see cref="QueueState.Failed"/>
    /// stehen, der Doktor meldet sie, und <c>queue --flush</c> nimmt sie wieder auf. Ohne eine
    /// Obergrenze klopfte ein hoffnungsloser Eintrag bis in alle Ewigkeit an eine Instanz, die
    /// ihn ohnehin abweist.
    /// </remarks>
    public const int MaxAttempts = 10;

    /// <summary>Wie lange auf die Dateisperre gewartet wird.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Pause zwischen zwei Versuchen, die Sperre zu bekommen.</summary>
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly string _path;
    private readonly string _lockPath;
    private readonly TimeProvider _time;

    /// <summary>Öffnet die Warteschlange an einem beliebigen Pfad.</summary>
    /// <param name="path">Die Datei.</param>
    /// <param name="time">Die Zeitquelle; für Tests austauschbar.</param>
    public CommitOutbox(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _lockPath = _path + ".lock";
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Öffnet die Warteschlange am vorgesehenen Ort.</summary>
    public static CommitOutbox Default(TimeProvider? time = null) =>
        new(StoragePaths.QueueFile, time);

    /// <summary>Der Pfad der Datei.</summary>
    public string Path => _path;

    /// <summary>Alle Einträge, so wie sie auf der Platte stehen.</summary>
    public IReadOnlyList<QueuedCommit> All() => Read().Entries;

    /// <summary>
    /// Reiht einen Commit ein — es sei denn, er steht schon da.
    /// </summary>
    /// <param name="payload">Die fertige Fernwartung.</param>
    /// <param name="repository">Der Name des Repositorys, für die Anzeige.</param>
    /// <param name="subject">Der Betreff des Commits, für die Anzeige.</param>
    public EnqueueResult Enqueue(RemoteSupportWrite payload, string repository = "",
                                 string subject = "")
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.RemoteMaintenanceId);

        EnqueueResult result = new(Added: false, Existing: null);

        Mutate(file =>
        {
            QueuedCommit? existing = Find(file, payload.RemoteMaintenanceId);
            if (existing is not null)
            {
                result = new EnqueueResult(Added: false, existing);
                return file;
            }

            DateTimeOffset now = _time.GetUtcNow();
            QueuedCommit entry = new()
            {
                RemoteMaintenanceId = payload.RemoteMaintenanceId,
                Payload = payload,
                State = QueueState.Pending,
                CreatedAt = now,

                // Faellig ab sofort: Der Haken versucht unmittelbar nach dem Einreihen zu
                // senden, und ein Rueckstau von Anfang an hiesse, dass der erste Versuch
                // immer ausfaellt.
                NextAttemptAt = now,
                Repository = repository,
                Subject = subject,
            };

            result = new EnqueueResult(Added: true, Existing: null);
            return file with { Entries = [.. file.Entries, entry] };
        });

        return result;
    }

    /// <summary>Die Einträge, die jetzt an der Reihe sind — älteste zuerst.</summary>
    /// <remarks>
    /// Aufgegebene Einträge sind <b>nicht</b> dabei. Sie kommen erst wieder mit, wenn jemand
    /// sie über <see cref="Revive"/> aufnimmt — sonst liefe jeder Commit gegen dieselbe Wand
    /// wie der aufgegebene zuvor.
    /// </remarks>
    public IReadOnlyList<QueuedCommit> Due()
    {
        DateTimeOffset now = _time.GetUtcNow();

        return [.. Read().Entries
            .Where(entry => entry.State == QueueState.Pending && entry.NextAttemptAt <= now)
            .OrderBy(entry => entry.CreatedAt)];
    }

    /// <summary>Vermerkt, dass TANSS die Fernwartung angelegt hat.</summary>
    /// <param name="remoteMaintenanceId">Der Commit-Hash.</param>
    /// <param name="remoteSupportId">Die Kennung, die TANSS vergeben hat.</param>
    public void Complete(string remoteMaintenanceId, int remoteSupportId = 0) =>
        Update(remoteMaintenanceId, entry => entry with
        {
            State = QueueState.Done,
            CompletedAt = _time.GetUtcNow(),
            RemoteSupportId = remoteSupportId > 0 ? remoteSupportId : null,
            LastError = null,
            OutcomeUnknown = false,
        });

    /// <summary>
    /// Vermerkt einen misslungenen Versuch und setzt den Rückstau.
    /// </summary>
    /// <param name="remoteMaintenanceId">Der Commit-Hash.</param>
    /// <param name="error">Die bereits geschwärzte Fehlermeldung.</param>
    /// <param name="outcomeUnknown">
    /// Ist offen, ob TANSS die Fernwartung trotzdem angelegt hat? Bei einer Zeitüberschreitung
    /// und bei einem Verbindungsabbruch <b>ja</b> — und dann steht vor der Wiederholung die
    /// Existenzprüfung.
    /// </param>
    public void Fail(string remoteMaintenanceId, string error, bool outcomeUnknown = false) =>
        Update(remoteMaintenanceId, entry =>
        {
            int attempts = entry.Attempts + 1;
            DateTimeOffset now = _time.GetUtcNow();

            return entry with
            {
                Attempts = attempts,
                State = attempts >= MaxAttempts ? QueueState.Failed : QueueState.Pending,
                NextAttemptAt = Backoff.NextAttemptAfter(now, attempts),
                LastError = error,

                // Einmal ungeklaert, immer ungeklaert: Ein spaeterer Fehlschlag mit klarem
                // Ausgang hebt nicht auf, dass ein frueherer Versuch die Anfrage abgesetzt
                // haben koennte.
                OutcomeUnknown = entry.OutcomeUnknown || outcomeUnknown,
            };
        });

    /// <summary>Nimmt aufgegebene Einträge wieder auf.</summary>
    /// <returns>Wie viele Einträge wieder wartend sind.</returns>
    public int Revive()
    {
        int revived = 0;

        Mutate(file =>
        {
            DateTimeOffset now = _time.GetUtcNow();

            return file with
            {
                Entries = [.. file.Entries.Select(entry =>
                {
                    if (entry.State != QueueState.Failed)
                    {
                        return entry;
                    }

                    revived++;
                    return entry with
                    {
                        State = QueueState.Pending,
                        Attempts = 0,
                        NextAttemptAt = now,
                    };
                })],
            };
        });

        return revived;
    }

    /// <summary>
    /// Nimmt erledigte Einträge fort, die älter sind als die Frist.
    /// </summary>
    /// <remarks>
    /// <b>Nur erledigte.</b> Wartende und aufgegebene Einträge bleiben stehen, gleich wie alt
    /// sie werden: Sie sind ungebuchte Arbeitszeit. Steht die Instanz eine Woche still, ist ein
    /// Eintrag irgendwann älter als die Frist — fortgeräumt wird er trotzdem nicht.
    /// </remarks>
    /// <param name="retention">Die Frist. Null oder negativ räumt beim nächsten Lauf alles Erledigte fort.</param>
    /// <returns>Wie viele Einträge fortgenommen wurden.</returns>
    public int Prune(TimeSpan retention)
    {
        int removed = 0;

        Mutate(file =>
        {
            DateTimeOffset limit = _time.GetUtcNow() - (retention > TimeSpan.Zero ? retention : TimeSpan.Zero);

            List<QueuedCommit> kept = [.. file.Entries.Where(entry =>
                entry.State != QueueState.Done
                || (entry.CompletedAt ?? entry.CreatedAt) > limit)];

            removed = file.Entries.Count - kept.Count;
            return file with { Entries = kept };
        });

        return removed;
    }

    /// <summary>Sucht einen Eintrag.</summary>
    public QueuedCommit? Find(string remoteMaintenanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMaintenanceId);
        return Find(Read(), remoteMaintenanceId);
    }

    private static QueuedCommit? Find(QueueFile file, string remoteMaintenanceId) =>
        file.Entries.FirstOrDefault(entry => string.Equals(
            entry.RemoteMaintenanceId, remoteMaintenanceId, StringComparison.OrdinalIgnoreCase));

    private void Update(string remoteMaintenanceId, Func<QueuedCommit, QueuedCommit> change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMaintenanceId);

        Mutate(file => file with
        {
            Entries = [.. file.Entries.Select(entry => string.Equals(
                entry.RemoteMaintenanceId, remoteMaintenanceId, StringComparison.OrdinalIgnoreCase)
                    ? change(entry)
                    : entry)],
        });
    }

    /// <summary>Liest die Datei. Fehlt sie, ist die Warteschlange leer.</summary>
    private QueueFile Read()
    {
        if (!File.Exists(_path))
        {
            return new QueueFile();
        }

        string raw;
        try
        {
            raw = File.ReadAllText(_path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new QueueException(
                $"{_path} liess sich nicht lesen: {exception.Message}. In dieser Datei steht "
                + "ungebuchte Arbeitszeit; sie zu übergehen hiesse, sie zu verlieren.", exception);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new QueueFile();
        }

        QueueFile file;
        try
        {
            file = JsonSerializer.Deserialize<QueueFile>(raw, Json) ?? new QueueFile();
        }
        catch (JsonException exception)
        {
            throw new QueueException(
                $"{_path} ist kein gültiges JSON: {exception.Message} Die Datei trägt ungebuchte "
                + "Arbeitszeit — sie gehört gesichert und geprüft, nicht gelöscht.", exception);
        }

        return file.Version <= QueueFile.CurrentVersion
            ? file
            : throw new QueueException(
                $"{_path} hat die Fassung {file.Version}; diese Programmfassung liest bis "
                + $"{QueueFile.CurrentVersion}. Die Datei stammt aus einer neueren Fassung des "
                + "Werkzeugs. Sie wird nicht angefasst: Ein älterer Stand würde beim nächsten "
                + "Speichern Felder fortschreiben, die er nicht kennt.");
    }

    /// <summary>
    /// Liest, ändert und schreibt die Datei unter einer Sperre.
    /// </summary>
    /// <remarks>
    /// Die Sperre ist eine eigene Datei neben der Warteschlange, die exklusiv geöffnet wird.
    /// Die Warteschlange selbst zu sperren ginge nicht: Sie wird beim Schreiben umbenannt, und
    /// eine Sperre auf einer Datei, die gleich ersetzt wird, schützt nichts.
    /// </remarks>
    private void Mutate(Func<QueueFile, QueueFile> change)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

        using FileStream guard = AcquireLock();

        QueueFile updated = change(Read());
        AtomicFile.WriteText(_path, JsonSerializer.Serialize(updated, Json) + "\n");
    }

    private FileStream AcquireLock()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + LockTimeout;

        while (true)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                      FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                // Ein anderer Aufruf schreibt gerade. Das dauert Millisekunden.
                Thread.Sleep(LockRetryDelay);
            }
            catch (IOException exception)
            {
                throw new QueueException(
                    $"Die Warteschlange {_path} war {LockTimeout.TotalSeconds:0} Sekunden lang "
                    + "gesperrt. Läuft ein zweiter Vorgang, der nicht fertig wird? Der Commit "
                    + "ist nicht verloren — er lässt sich mit „tanss-git book <Commit>“ "
                    + "nachreichen.", exception);
            }
        }
    }
}
