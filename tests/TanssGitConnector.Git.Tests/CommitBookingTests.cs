using TanssGitConnector.Api;
using TanssGitConnector.Api.Model;
using Xunit;

namespace TanssGitConnector.Git.Tests;

/// <summary>
/// Dauer und Kommentar einer gebuchten Fernwartung.
/// </summary>
/// <remarks>
/// Hier hängt die Abrechnung dran. Die Grenzen sind deshalb kein Feinschliff: Ohne Obergrenze
/// bucht der erste Commit nach dem Wochenende zweiundsiebzig Stunden.
/// </remarks>
public class CommitBookingTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly BookingSettings Fixed = new()
    {
        RemoteSupportTypeId = 1007,
        EmployeeId = 42,
        Mode = DurationMode.Fixed,
        Fixed = TimeSpan.FromMinutes(15),
    };

    private static readonly BookingSettings Elapsed = Fixed with
    {
        Mode = DurationMode.SinceLastCommit,
        Minimum = TimeSpan.FromMinutes(5),
        Maximum = TimeSpan.FromHours(2),
    };

    [Fact]
    public void Feste_Dauer_bucht_immer_dasselbe()
    {
        Assert.Equal(TimeSpan.FromMinutes(15),
            CommitBooking.DurationFor(Commit(), Noon.AddHours(-9), Fixed));
    }

    [Fact]
    public void Seit_dem_letzten_Commit_wird_gemessen()
    {
        Assert.Equal(TimeSpan.FromMinutes(37),
            CommitBooking.DurationFor(Commit(), Noon.AddMinutes(-37), Elapsed));
    }

    [Fact]
    public void Die_Obergrenze_faengt_das_Wochenende_ab()
    {
        Assert.Equal(TimeSpan.FromHours(2),
            CommitBooking.DurationFor(Commit(), Noon.AddDays(-3), Elapsed));
    }

    [Fact]
    public void Die_Untergrenze_faengt_den_Tippfehler_Commit_ab()
    {
        Assert.Equal(TimeSpan.FromMinutes(5),
            CommitBooking.DurationFor(Commit(), Noon.AddSeconds(-20), Elapsed));
    }

    /// <summary>
    /// Ohne Vorgänger gilt die Vorgabe.
    /// </summary>
    /// <remarks>
    /// Der Wurzelcommit hat keinen. Eine gerechnete Null wäre eine Fernwartung ohne Dauer — in
    /// TANSS ein Datensatz, der nichts belegt.
    /// </remarks>
    [Fact]
    public void Ohne_Vorgaenger_gilt_die_Vorgabe()
    {
        Assert.Equal(TimeSpan.FromMinutes(15),
            CommitBooking.DurationFor(Commit(), null, Elapsed));
    }

    /// <summary>
    /// Eine rückwärts laufende Uhr ergibt die Vorgabe, keine negative Dauer.
    /// </summary>
    /// <remarks>
    /// Nach einem <c>rebase</c> ist die Zeitfolge der Commits nicht mehr ihre Reihenfolge. Das
    /// ist der Normalfall und kein Fehler.
    /// </remarks>
    [Fact]
    public void Ein_spaeterer_Vorgaenger_ergibt_die_Vorgabe()
    {
        Assert.Equal(TimeSpan.FromMinutes(15),
            CommitBooking.DurationFor(Commit(), Noon.AddMinutes(30), Elapsed));
    }

    [Fact]
    public void Gebucht_wird_rueckwaerts_vom_Commit()
    {
        RemoteSupportWrite payload = CommitBooking.Build(Commit(), null, 0, Fixed);

        Assert.Equal(TanssTime.ToUnixSeconds(Noon), payload.EndTime);
        Assert.Equal(TanssTime.ToUnixSeconds(Noon.AddMinutes(-15)), payload.StartTime);
    }

    [Fact]
    public void Die_Vorgangskennung_ist_der_volle_Hash()
    {
        RemoteSupportWrite payload = CommitBooking.Build(Commit(), null, 0, Fixed);

        Assert.Equal(Commit().Sha, payload.RemoteMaintenanceId);
        Assert.Equal(40, payload.RemoteMaintenanceId.Length);
    }

    [Fact]
    public void Ohne_Ticket_geht_eine_Null_hinaus()
    {
        Assert.Equal(0, CommitBooking.Build(Commit(), null, 0, Fixed).TicketId);
        Assert.Equal(5000, CommitBooking.Build(Commit(), null, 5000, Fixed).TicketId);
    }

    /// <summary>
    /// Eine Gerätekennung wird niemals gesetzt.
    /// </summary>
    /// <remarks>
    /// TANSS übersetzt sie in eine Firma. Ein Commit gehört zu keinem Gerät des Kunden; eine
    /// erfundene Kennung buchte die Arbeitszeit einer beliebigen Firma zu.
    /// </remarks>
    [Fact]
    public void Eine_Geraetekennung_wird_nie_gesetzt()
    {
        Assert.Null(CommitBooking.Build(Commit(), null, 0, Fixed).DeviceId);
    }

    [Fact]
    public void Der_Kommentar_beginnt_mit_dem_Betreff()
    {
        string comment = CommitBooking.Comment(Commit(), Fixed);

        Assert.StartsWith("Rechnungslauf korrigiert", comment, StringComparison.Ordinal);
        Assert.Contains("Der Stundensatz kam aus dem falschen Vertrag.", comment,
                        StringComparison.Ordinal);
        Assert.Contains("Commit 3f2a1bc", comment, StringComparison.Ordinal);
        Assert.Contains("Repository abrechnung", comment, StringComparison.Ordinal);
        Assert.Contains("Zweig feature/rechnungslauf#5000", comment, StringComparison.Ordinal);
    }

    [Fact]
    public void Abgeschaltete_Angaben_stehen_nicht_im_Kommentar()
    {
        BookingSettings settings = Fixed with
        {
            IncludeBody = false,
            IncludeBranch = false,
            IncludeRepository = false,
        };

        string comment = CommitBooking.Comment(Commit(), settings);

        Assert.DoesNotContain("Stundensatz", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("Zweig", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("Repository", comment, StringComparison.Ordinal);
        Assert.Contains("Commit 3f2a1bc", comment, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_Commit_ohne_Meldung_ergibt_keinen_leeren_Kommentar()
    {
        string comment = CommitBooking.Comment(Commit() with { Subject = "", Message = "" }, Fixed);

        Assert.StartsWith("Commit ohne Meldung", comment, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_ueberlanger_Kommentar_wird_gekuerzt_und_sagt_es()
    {
        CommitInfo huge = Commit() with
        {
            Message = "Betreff\n\n" + new string('x', CommitBooking.MaxCommentLength * 2),
        };

        string comment = CommitBooking.Comment(huge, Fixed);

        Assert.Equal(CommitBooking.MaxCommentLength, comment.Length);
        Assert.EndsWith(CommitBooking.TruncationNotice, comment, StringComparison.Ordinal);
    }

    private static CommitInfo Commit() => new()
    {
        Sha = "3f2a1bc9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f4a3",
        Parents = "aaa111",
        CommittedAt = Noon,
        AuthorName = "Erika Mustermann",
        AuthorEmail = "e.mustermann@example.de",
        Subject = "Rechnungslauf korrigiert",
        Message = "Rechnungslauf korrigiert\n\nDer Stundensatz kam aus dem falschen Vertrag.",
        Branch = "feature/rechnungslauf#5000",
        RepositoryName = "abrechnung",
        RepositoryPath = "/home/x/abrechnung",
    };
}
