using System.Text;
using TanssGitConnector.Storage.Logging;
using TanssGitConnector.Storage.Secrets;
using Xunit;

namespace TanssGitConnector.Storage.Tests;

/// <summary>
/// Tokenspeicher, Protokoll und das atomare Schreiben darunter.
/// </summary>
public class TokenAndLogTests
{
    /// <summary>
    /// Das Präfix wird an genau einer Stelle ergänzt.
    /// </summary>
    /// <remarks>
    /// TANSS liefert es beim Prägen mit, bei der Anmeldung nicht. Ohne Präfix antwortet die
    /// Instanz auf jede Anfrage mit 403 — ununterscheidbar von einem abgelaufenen Token.
    /// </remarks>
    [Theory]
    [InlineData("abc", "Bearer abc")]
    [InlineData("Bearer abc", "Bearer abc")]
    [InlineData("  Bearer   abc  ", "Bearer abc")]
    [InlineData("bearer abc", "Bearer abc")]
    public void Das_Bearer_Praefix_wird_vereinheitlicht(string input, string expected) =>
        Assert.Equal(expected, TokenProtection.Normalize(input));

    [Fact]
    public void Der_Dateispeicher_schreibt_liest_und_sichert()
    {
        using Sandbox sandbox = new();
        FileTokenStore store = new(sandbox.File("credentials.dat"));

        store.Write("erstes-token");
        Assert.Equal("Bearer erstes-token", store.Read());

        store.Write("zweites-token");
        Assert.Equal("Bearer zweites-token", store.Read());

        // Vor jedem Schreiben entsteht eine Sicherung: Der gefaehrliche Moment ist die
        // Erneuerung - waere der neue Wert unbrauchbar und der alte schon fort, gaebe es gar
        // keinen Zugang mehr.
        Assert.True(File.Exists(store.BackupPath));
        Assert.Contains("erstes-token", File.ReadAllText(store.BackupPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Ohne_Token_nennt_die_Meldung_die_Einrichtung()
    {
        using Sandbox sandbox = new();
        FileTokenStore store = new(sandbox.File("credentials.dat"));

        TokenStoreException error = Assert.Throws<TokenStoreException>(store.Read);

        Assert.Contains("tanss-git setup", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_leeres_Token_ist_ein_Fehler_und_kein_leerer_Wert()
    {
        using Sandbox sandbox = new();
        File.WriteAllBytes(sandbox.File("credentials.dat"), []);

        FileTokenStore store = new(sandbox.File("credentials.dat"));

        _ = Assert.Throws<TokenStoreException>(store.Read);
    }

    [Fact]
    public void Atomar_geschrieben_heisst_entweder_alt_oder_neu()
    {
        using Sandbox sandbox = new();
        string path = sandbox.File("datei.txt");

        AtomicFile.WriteText(path, "alt");

        _ = Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path,
            _ => throw new InvalidOperationException("mitten im Schreiben")));

        Assert.Equal("alt", File.ReadAllText(path));

        // Die Zwischendatei wird aufgeraeumt; im Verzeichnis bleibt genau die Zieldatei.
        Assert.Single(Directory.GetFiles(sandbox.Path));
    }

    [Fact]
    public void Das_Protokoll_haelt_fest_warum()
    {
        using Sandbox sandbox = new();
        ActivityLog log = new(sandbox.File("log.jsonl"), new TestClock());

        log.Info("book.queued", "Eingereiht.", "3f2a1bc9d8e7", "abrechnung", 5000);
        log.Warning("book.ticket-missing", "Ticket 5000 gibt es nicht.", null, null, 5000);

        IReadOnlyList<LogEntry> entries = log.Tail();

        // Juengste zuerst: Wer das Protokoll aufschlaegt, sucht fast immer das Letzte.
        Assert.Equal("book.ticket-missing", entries[0].Event);
        Assert.Equal("book.queued", entries[1].Event);
        Assert.Equal("3f2a1bc", entries[1].Commit);
        Assert.Equal(5000, entries[1].TicketId);
    }

    /// <summary>
    /// Geheimnisse werden niemals protokolliert.
    /// </summary>
    /// <remarks>
    /// Das gilt immer und lässt sich nicht abschalten. Eine einzige Zeile mit vollständigem
    /// Token ist ein dauerhafter Schlüsselverlust — TANSS 10.10.0 kennt keinen Widerruf.
    /// </remarks>
    [Fact]
    public void Ein_Token_landet_nie_im_Protokoll()
    {
        using Sandbox sandbox = new();
        ActivityLog log = new(sandbox.File("log.jsonl"), new TestClock());

        log.Error("queue.rejected",
            "Abgewiesen mit apiToken: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiI0MiJ9.abcDEF");

        string raw = File.ReadAllText(sandbox.File("log.jsonl"), Encoding.UTF8);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", raw, StringComparison.Ordinal);
        Assert.Contains("Abgewiesen", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Alte_Zeilen_werden_fortgenommen_neue_bleiben()
    {
        using Sandbox sandbox = new();
        TestClock clock = new();
        ActivityLog log = new(sandbox.File("log.jsonl"), clock);

        log.Info("alt", "Eine alte Zeile.");
        clock.Advance(TimeSpan.FromDays(60));
        log.Info("neu", "Eine neue Zeile.");

        Assert.Equal(1, log.Prune(TimeSpan.FromDays(30)));

        IReadOnlyList<LogEntry> entries = log.Tail();
        Assert.Single(entries);
        Assert.Equal("neu", entries[0].Event);
    }

    /// <summary>
    /// Eine unlesbare Zeile bleibt stehen.
    /// </summary>
    /// <remarks>
    /// Was sich nicht lesen lässt, lässt sich auch nicht datieren — und eine Zeile
    /// fortzunehmen, deren Alter niemand kennt, hiesse raten.
    /// </remarks>
    [Fact]
    public void Eine_unlesbare_Zeile_bleibt_stehen()
    {
        using Sandbox sandbox = new();
        TestClock clock = new();
        ActivityLog log = new(sandbox.File("log.jsonl"), clock);

        File.AppendAllText(sandbox.File("log.jsonl"), "kaputte Zeile\n");
        log.Info("neu", "Eine Zeile.");

        clock.Advance(TimeSpan.FromDays(60));
        _ = log.Prune(TimeSpan.FromDays(30));

        Assert.Contains("kaputte Zeile",
            File.ReadAllText(sandbox.File("log.jsonl")), StringComparison.Ordinal);
    }

    /// <summary>
    /// Ein Fehler beim Protokollieren kostet nichts.
    /// </summary>
    /// <remarks>
    /// Ein Commit, der nicht gebucht wird, weil das Protokoll klemmt, wäre die Umkehrung aller
    /// Verhältnisse.
    /// </remarks>
    [Fact]
    public void Ein_unschreibbares_Protokoll_wirft_nicht()
    {
        using Sandbox sandbox = new();

        // Ein Verzeichnis an der Stelle der Datei: Das Anhaengen kann nicht gelingen.
        _ = Directory.CreateDirectory(sandbox.File("log.jsonl"));

        ActivityLog log = new(sandbox.File("log.jsonl"), new TestClock());

        log.Info("book.queued", "Eingereiht.");
    }
}
