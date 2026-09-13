using System.Text.Json.Serialization;
using TanssGitConnector.Api.Model;

namespace TanssGitConnector.Storage.Queue;

/// <summary>Zustand eines Warteschlangeneintrags.</summary>
public enum QueueState
{
    /// <summary>Wartet auf den nächsten fälligen Versuch.</summary>
    Pending,

    /// <summary>Bei TANSS angekommen.</summary>
    /// <remarks>
    /// Die Zeile bleibt stehen, bis die Aufbewahrungsfrist sie fortnimmt. Sie ist der Beleg,
    /// dass dieser Commit gebucht ist — und der Riegel gegen eine zweite Buchung, wenn jemand
    /// denselben Commit von Hand nachreicht.
    /// </remarks>
    Done,

    /// <summary>
    /// Aufgegeben. Wird nicht mehr von selbst versucht.
    /// </summary>
    /// <remarks>
    /// Nicht gelöscht, sondern aufgegeben: Hier steht ungebuchte Arbeitszeit, und die
    /// verschwindet nicht dadurch, dass niemand mehr hinsieht. <c>tanss-git doctor</c> meldet
    /// solche Zeilen, und <c>tanss-git queue --flush</c> nimmt sie wieder auf.
    /// </remarks>
    Failed,
}

/// <summary>Ein Eintrag der Warteschlange.</summary>
/// <remarks>
/// Die Nutzlast ist ein fertiges <see cref="RemoteSupportWrite"/>. Sie entsteht genau einmal,
/// im <c>post-commit</c>-Hook, und wird danach nicht mehr angefasst. Ein Eintrag, dessen
/// Nutzlast beim Senden nachgerechnet würde, hinge von Einstellungen ab, die sich seither
/// geändert haben können — und die gebuchte Fernwartung wäre nicht mehr die, die der Techniker
/// beim Commit gesehen hat.
/// </remarks>
public sealed record QueuedCommit
{
    /// <summary>Der Commit-Hash. Zugleich der Schlüssel dieser Zeile und die Kennung in TANSS.</summary>
    public required string RemoteMaintenanceId { get; init; }

    /// <summary>Die Fernwartung, so wie sie an TANSS geht.</summary>
    public required RemoteSupportWrite Payload { get; init; }

    /// <summary>Zustand.</summary>
    public QueueState State { get; init; } = QueueState.Pending;

    /// <summary>Bisherige Sendeversuche.</summary>
    public int Attempts { get; init; }

    /// <summary>Wann der Eintrag in die Warteschlange kam.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Ab wann er wieder versucht werden darf.</summary>
    public DateTimeOffset NextAttemptAt { get; init; }

    /// <summary>Wann er abgeschlossen wurde.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Die Kennung, die TANSS beim Anlegen vergeben hat.</summary>
    public int? RemoteSupportId { get; init; }

    /// <summary>Der zuletzt gemeldete Fehler, bereits geschwärzt.</summary>
    public string? LastError { get; init; }

    /// <summary>
    /// Der Ausgang des letzten Sendeversuchs ist ungeklärt.
    /// </summary>
    /// <remarks>
    /// <para><b>Das wichtigste Feld dieser Zeile.</b> Es steht für den Fall, in dem die Anfrage
    /// hinausging, die Antwort aber nie ankam — Zeitüberschreitung, abgerissene Verbindung,
    /// Absturz mitten im Senden. Ob TANSS die Fernwartung angelegt hat, weiß von hier aus
    /// niemand.</para>
    /// <para>Vor jeder Wiederholung einer solchen Zeile steht deshalb die Existenzprüfung.
    /// TANSS dedupliziert nicht: Ein zweiter Aufruf mit derselben Kennung erzeugt einen zweiten
    /// Datensatz, und der ist nur über einen direkten Datenbankzugriff wieder zu entfernen.</para>
    /// </remarks>
    public bool OutcomeUnknown { get; init; }

    /// <summary>Der Name des Repositorys — für die Anzeige in <c>tanss-git queue</c>.</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>Der Betreff des Commits — für die Anzeige in <c>tanss-git queue</c>.</summary>
    /// <remarks>
    /// Steht neben der Nutzlast, obwohl er auch in deren Kommentar steckt: Die Anzeige soll
    /// nicht in einem Text suchen müssen, dessen Aufbau sich ändern darf.
    /// </remarks>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Die ersten sieben Zeichen des Hashs.</summary>
    [JsonIgnore]
    public string ShortSha => RemoteMaintenanceId.Length <= 7
        ? RemoteMaintenanceId
        : RemoteMaintenanceId[..7];
}

/// <summary>Der Inhalt der Warteschlangendatei.</summary>
/// <remarks>
/// Eine Datei statt einer Datenbank, und das mit Absicht: Der häufigste Aufrufer ist ein
/// <c>post-commit</c>-Hook, der in Millisekunden anlaufen muss. Eine eingebettete Datenbank
/// kostet beim ersten Zugriff ein Vielfaches davon, und die Menge — eine Handvoll Zeilen,
/// selten mehr — rechtfertigt sie nicht.
/// </remarks>
public sealed record QueueFile
{
    /// <summary>Höchster Stand, den diese Programmversion liest.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Stand des Dateiaufbaus.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Die Einträge, jüngste zuletzt.</summary>
    public IReadOnlyList<QueuedCommit> Entries { get; init; } = [];
}
