using Xunit;

namespace TanssGitConnector.Git.Tests;

/// <summary>
/// Woher die Ticketnummer kommt — und woher ausdrücklich nicht.
/// </summary>
/// <remarks>
/// Die Falschtreffer sind hier wichtiger als die Treffer. Eine Fernwartung am falschen Ticket
/// steht beim falschen Kunden in der Abrechnung, und darauf kommt niemand von selbst.
/// </remarks>
public class TicketNumberTests
{
    [Theory]
    [InlineData("feature/rechnungslauf#5000", 5000)]
    [InlineData("#42", 42)]
    [InlineData("bugfix/kunde/2026-09#123456", 123456)]
    public void Zweig_der_auf_die_Nummer_endet_wird_gelesen(string branch, int expected) =>
        Assert.Equal(expected, TicketNumbers.FromBranch(branch));

    [Theory]
    [InlineData("main")]
    [InlineData("")]
    [InlineData("fix/#2-spalten-layout")]   // Zwei Spalten, kein Ticket.
    [InlineData("feature/5000")]            // Ohne Raute ist es eine Nummer im Namen.
    [InlineData("feature/xy#")]
    [InlineData("feature/xy#0")]
    [InlineData("release/2026#20260913120000")] // Ein Zeitstempel ist keine Ticketnummer.
    public void Alles_andere_ergibt_keine_Nummer(string branch) =>
        Assert.Equal(0, TicketNumbers.FromBranch(branch));

    [Theory]
    [InlineData("Betreff\n\nTicket: 5000")]
    [InlineData("Betreff\n\nticket:5000")]
    [InlineData("Betreff\n\nTANSS: #5000")]
    [InlineData("Betreff\n\nTANSS-Ticket: 5000\n")]
    public void Eine_eigene_Zeile_in_der_Meldung_wird_gelesen(string message) =>
        Assert.Equal(5000, TicketNumbers.FromMessage(message));

    /// <summary>
    /// Ein Verweis mitten im Satz zählt nicht.
    /// </summary>
    /// <remarks>
    /// <c>#42</c> in der Prosa ist fast immer eine Nummer aus einem Fremdsystem — GitHub,
    /// GitLab und Jira schreiben ihre Verweise genau so.
    /// </remarks>
    [Theory]
    [InlineData("Behebt #42 endlich")]
    [InlineData("Siehe Ticket 42 im anderen System")]
    [InlineData("Betreff\n\nDas Ticket: siehe unten")]
    public void Ein_Verweis_im_Satz_zaehlt_nicht(string message) =>
        Assert.Equal(0, TicketNumbers.FromMessage(message));

    [Fact]
    public void Bei_mehreren_Zeilen_gewinnt_die_letzte()
    {
        Assert.Equal(7002, TicketNumbers.FromMessage("Betreff\n\nTicket: 7001\nTicket: 7002"));
    }

    [Fact]
    public void Die_Meldung_schlaegt_den_Zweignamen()
    {
        CommitInfo commit = Commit("feature/sammelzweig#1000", "Betreff\n\nTicket: 2000");

        TicketReference ticket = TicketNumbers.Resolve(commit);

        Assert.Equal(2000, ticket.TicketId);
        Assert.Equal(TicketSource.Trailer, ticket.Source);
    }

    [Fact]
    public void Die_Hand_schlaegt_alles()
    {
        CommitInfo commit = Commit("feature/sammelzweig#1000", "Betreff\n\nTicket: 2000");

        TicketReference ticket = TicketNumbers.Resolve(commit, explicitTicketId: 3000);

        Assert.Equal(3000, ticket.TicketId);
        Assert.Equal(TicketSource.Explicit, ticket.Source);
    }

    [Fact]
    public void Abgeschaltete_Quellen_werden_nicht_befragt()
    {
        CommitInfo commit = Commit("feature/xy#1000", "Betreff\n\nTicket: 2000");

        TicketReference ticket = TicketNumbers.Resolve(commit, 0, fromBranch: false,
                                                       fromMessage: false);

        Assert.False(ticket.HasTicket);
        Assert.Equal(TicketSource.None, ticket.Source);
    }

    [Fact]
    public void Ohne_Fund_bleibt_es_bei_keiner_Nummer()
    {
        TicketReference ticket = TicketNumbers.Resolve(Commit("main", "Betreff"));

        Assert.False(ticket.HasTicket);
        Assert.Equal("ohne Ticketbezug", ticket.Describe());
    }

    private static CommitInfo Commit(string branch, string message) => new()
    {
        Sha = "3f2a1bc9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f4a3",
        CommittedAt = DateTimeOffset.FromUnixTimeSeconds(1757800000),
        Branch = branch,
        Message = message,
        Subject = message.Split('\n')[0],
    };
}
