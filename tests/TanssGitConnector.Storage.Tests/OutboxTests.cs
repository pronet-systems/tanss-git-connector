using TanssGitConnector.Api.Model;
using TanssGitConnector.Storage.Queue;
using Xunit;

namespace TanssGitConnector.Storage.Tests;

/// <summary>
/// Die Warteschlange: Sie trägt ungebuchte Arbeitszeit, und daraus folgt fast alles.
/// </summary>
public class OutboxTests
{
    private const string Sha = "3f2a1bc9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f4a3";

    private static RemoteSupportWrite Payload(string sha = Sha) => new()
    {
        TypeId = 1007,
        EmployeeId = 42,
        StartTime = 1757800000,
        EndTime = 1757800900,
        RemoteMaintenanceId = sha,
        Comment = "Rechnungslauf korrigiert",
    };

    [Fact]
    public void Ein_Commit_wird_eingereiht_und_ist_sofort_faellig()
    {
        using Sandbox sandbox = new();
        CommitOutbox outbox = new(sandbox.File("queue.json"), new TestClock());

        EnqueueResult result = outbox.Enqueue(Payload(), "abrechnung", "Rechnungslauf korrigiert");

        Assert.True(result.Added);
        Assert.Single(outbox.Due());
        Assert.Equal("abrechnung", outbox.All()[0].Repository);
    }

    /// <summary>
    /// Derselbe Commit wird kein zweites Mal eingereiht.
    /// </summary>
    /// <remarks>
    /// Der Hash ist der Schlüssel. Ein zweiter Eintrag hiesse eine zweite Fernwartung — und
    /// TANSS dedupliziert nicht.
    /// </remarks>
    [Fact]
    public void Derselbe_Commit_wird_nicht_zweimal_eingereiht()
    {
        using Sandbox sandbox = new();
        CommitOutbox outbox = new(sandbox.File("queue.json"), new TestClock());

        _ = outbox.Enqueue(Payload());
        EnqueueResult second = outbox.Enqueue(Payload());

        Assert.False(second.Added);
        Assert.NotNull(second.Existing);
        Assert.Single(outbox.All());
    }

    /// <summary>
    /// Auch ein bereits gebuchter Commit wird nicht noch einmal eingereiht.
    /// </summary>
    /// <remarks>
    /// Die erledigte Zeile bleibt stehen, bis die Frist sie fortnimmt — ohne sie wäre die
    /// Sperre nach dem ersten erfolgreichen Senden wieder offen, und ein Nachreichen von Hand
    /// erzeugte eine Dublette.
    /// </remarks>
    [Fact]
    public void Ein_gebuchter_Commit_wird_nicht_erneut_eingereiht()
    {
        using Sandbox sandbox = new();
        CommitOutbox outbox = new(sandbox.File("queue.json"), new TestClock());

        _ = outbox.Enqueue(Payload());
        outbox.Complete(Sha, 38584);

        Assert.False(outbox.Enqueue(Payload()).Added);
        Assert.Equal(QueueState.Done, outbox.All()[0].State);
        Assert.Equal(38584, outbox.All()[0].RemoteSupportId);
    }

    [Fact]
    public void Ein_Fehlschlag_verschiebt_den_naechsten_Versuch()
    {
        using Sandbox sandbox = new();
        TestClock clock = new();
        CommitOutbox outbox = new(sandbox.File("queue.json"), clock);

        _ = outbox.Enqueue(Payload());
        outbox.Fail(Sha, "TANSS nicht erreichbar.");

        Assert.Empty(outbox.Due());
        Assert.Equal(1, outbox.All()[0].Attempts);

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Single(outbox.Due());
    }

    /// <summary>
    /// Ein ungeklärter Ausgang bleibt ungeklärt.
    /// </summary>
    /// <remarks>
    /// Ein späterer Fehlschlag mit klarem Ausgang hebt nicht auf, dass ein früherer Versuch die
    /// Anfrage abgesetzt haben könnte. Genau daran hängt die Existenzprüfung.
    /// </remarks>
    [Fact]
    public void Ein_ungeklaerter_Ausgang_bleibt_stehen()
    {
        using Sandbox sandbox = new();
        CommitOutbox outbox = new(sandbox.File("queue.json"), new TestClock());

        _ = outbox.Enqueue(Payload());
        outbox.Fail(Sha, "Zeitüberschreitung.", outcomeUnknown: true);
        outbox.Fail(Sha, "Abgewiesen.", outcomeUnknown: false);

        Assert.True(outbox.All()[0].OutcomeUnknown);
    }

    [Fact]
    public void Nach_zu_vielen_Versuchen_wird_aufgegeben_und_nicht_geloescht()
    {
        using Sandbox sandbox = new();
        TestClock clock = new();
        CommitOutbox outbox = new(sandbox.File("queue.json"), clock);

        _ = outbox.Enqueue(Payload());

        for (int attempt = 0; attempt < CommitOutbox.MaxAttempts; attempt++)
        {
            outbox.Fail(Sha, "TANSS nicht erreichbar.");
            clock.Advance(TimeSpan.FromHours(2));
        }

        Assert.Equal(QueueState.Failed, outbox.All()[0].State);
        Assert.Empty(outbox.Due());

        // Aufgegeben heisst nicht geloescht: Hier steht ungebuchte Arbeitszeit.
        Assert.Single(outbox.All());
        Assert.Equal(1, outbox.Revive());
        Assert.Single(outbox.Due());
    }

    /// <summary>
    /// Aufgeräumt wird nur, was erledigt ist.
    /// </summary>
    /// <remarks>
    /// Wartende und aufgegebene Einträge bleiben stehen, gleich wie alt sie werden. Steht die
    /// Instanz eine Woche still, ist ein Eintrag irgendwann älter als die Frist — fortgeräumt
    /// wird er trotzdem nicht.
    /// </remarks>
    [Fact]
    public void Aufgeraeumt_wird_nur_Erledigtes()
    {
        using Sandbox sandbox = new();
        TestClock clock = new();
        CommitOutbox outbox = new(sandbox.File("queue.json"), clock);

        _ = outbox.Enqueue(Payload("aaa"));
        _ = outbox.Enqueue(Payload("bbb"));
        outbox.Complete("aaa");

        clock.Advance(TimeSpan.FromDays(90));

        Assert.Equal(1, outbox.Prune(TimeSpan.FromDays(30)));
        Assert.Single(outbox.All());
        Assert.Equal("bbb", outbox.All()[0].RemoteMaintenanceId);
    }

    [Fact]
    public void Eine_fehlende_Datei_ist_eine_leere_Warteschlange()
    {
        using Sandbox sandbox = new();
        CommitOutbox outbox = new(sandbox.File("queue.json"), new TestClock());

        Assert.Empty(outbox.All());
        Assert.Empty(outbox.Due());
    }

    /// <summary>
    /// Eine Datei aus einer neueren Programmfassung wird nicht angefasst.
    /// </summary>
    /// <remarks>
    /// Ein älterer Stand kennt die neueren Felder nicht und schriebe sie beim nächsten Speichern
    /// fort — wartende Commits wären danach unvollständig, und unvollständig heisst hier: nicht
    /// gebucht und nicht mehr auffindbar.
    /// </remarks>
    [Fact]
    public void Eine_neuere_Fassung_wird_nicht_gelesen()
    {
        using Sandbox sandbox = new();
        File.WriteAllText(sandbox.File("queue.json"), """{"version":99,"entries":[]}""");

        CommitOutbox outbox = new(sandbox.File("queue.json"), new TestClock());

        QueueException error = Assert.Throws<QueueException>(() => outbox.All());
        Assert.Contains("99", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Eine_unlesbare_Datei_wird_nicht_ueberschrieben()
    {
        using Sandbox sandbox = new();
        File.WriteAllText(sandbox.File("queue.json"), "kein JSON");

        CommitOutbox outbox = new(sandbox.File("queue.json"), new TestClock());

        _ = Assert.Throws<QueueException>(() => outbox.Enqueue(Payload()));
        Assert.Equal("kein JSON", File.ReadAllText(sandbox.File("queue.json")));
    }

    [Fact]
    public void Die_Nutzlast_uebersteht_das_Schreiben_und_Lesen()
    {
        using Sandbox sandbox = new();
        CommitOutbox outbox = new(sandbox.File("queue.json"), new TestClock());

        _ = outbox.Enqueue(Payload() with { TicketId = 5000, DeviceName = "abrechnung" });

        CommitOutbox reopened = new(sandbox.File("queue.json"), new TestClock());
        QueuedCommit entry = reopened.All()[0];

        Assert.Equal(5000, entry.Payload.TicketId);
        Assert.Equal(1007, entry.Payload.TypeId);
        Assert.Equal(1757800900, entry.Payload.EndTime);
        Assert.Equal("abrechnung", entry.Payload.DeviceName);
        Assert.Equal("Rechnungslauf korrigiert", entry.Payload.Comment);
    }
}
