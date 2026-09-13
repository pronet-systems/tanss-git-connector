using System.Globalization;
using System.Text.RegularExpressions;

namespace TanssGitConnector.Git;

/// <summary>Woher die Ticketnummer stammt.</summary>
/// <remarks>
/// Die Herkunft steht im Protokoll und in der Ausgabe des Hooks. Wer am Monatsende eine
/// Fernwartung am falschen Ticket findet, soll nicht raten müssen, welche der drei Quellen sie
/// dorthin gebracht hat.
/// </remarks>
public enum TicketSource
{
    /// <summary>Keine Nummer gefunden. Gebucht wird ohne Ticketbezug.</summary>
    None,

    /// <summary>Aus dem Namen des Zweigs, der auf <c>#&lt;Nummer&gt;</c> endet.</summary>
    Branch,

    /// <summary>Aus einer Zeile <c>Ticket: &lt;Nummer&gt;</c> in der Commit-Meldung.</summary>
    Trailer,

    /// <summary>Von Hand übergeben, etwa mit <c>--ticket</c>.</summary>
    Explicit,
}

/// <summary>Die gefundene Ticketnummer samt Herkunft.</summary>
/// <param name="TicketId">Die Nummer; 0 heißt „keine“.</param>
/// <param name="Source">Woher sie stammt.</param>
public sealed record TicketReference(int TicketId, TicketSource Source)
{
    /// <summary>Keine Nummer gefunden.</summary>
    public static TicketReference None { get; } = new(0, TicketSource.None);

    /// <summary>Liegt überhaupt eine Nummer vor?</summary>
    public bool HasTicket => TicketId > 0;

    /// <summary>Ein Satzteil für Protokoll und Ausgabe.</summary>
    public string Describe() => Source switch
    {
        TicketSource.Branch => $"Ticket {TicketId} (aus dem Zweignamen)",
        TicketSource.Trailer => $"Ticket {TicketId} (aus der Commit-Meldung)",
        TicketSource.Explicit => $"Ticket {TicketId} (von Hand angegeben)",
        _ => "ohne Ticketbezug",
    };
}

/// <summary>
/// Findet die Ticketnummer zu einem Commit.
/// </summary>
/// <remarks>
/// <para>Drei Quellen, in fester Rangfolge: eine von Hand übergebene Nummer schlägt die
/// Commit-Meldung, und die schlägt den Zweignamen. Der Zweigname gilt für alle Commits des
/// Zweigs, die Meldung für genau diesen einen — das Genauere gewinnt.</para>
///
/// <para><b>Warum der Zweigname auf der Nummer enden muss.</b> Eine Raute mit Ziffern mitten im
/// Namen ist kein verlässliches Zeichen: <c>fix/#2-spalten-layout</c> meint eine zweispaltige
/// Darstellung und kein Ticket. Die Regel „am Ende“ ist die, die sich in einem Satz erklären
/// lässt und die niemand versehentlich erfüllt.</para>
///
/// <para><b>Warum die Commit-Meldung nur auf eine eigene Zeile hört.</b> In <c>Betrifft #42</c>
/// steckt fast immer eine Nummer aus einem Fremdsystem — GitHub, GitLab, Jira schreiben ihre
/// Verweise genau so. Eine Zeile <c>Ticket: 42</c> dagegen schreibt niemand versehentlich. Auf
/// eine Fernwartung am falschen Ticket kommt niemand von selbst; sie steht beim falschen Kunden
/// in der Abrechnung.</para>
/// </remarks>
public static partial class TicketNumbers
{
    /// <summary>
    /// Die grösste Nummer, die noch als Ticketnummer durchgeht.
    /// </summary>
    /// <remarks>
    /// Neun Stellen. Alles darüber passt nicht mehr sicher in eine Ganzzahl, und ein Zweig, der
    /// auf <c>#20260913120000</c> endet, trägt einen Zeitstempel und keine Ticketnummer.
    /// </remarks>
    public const int Largest = 999_999_999;

    /// <summary>Liest die Nummer aus einem Zweignamen, der auf <c>#&lt;Nummer&gt;</c> endet.</summary>
    /// <param name="branch">Der Zweigname; leer bei abgetrenntem HEAD.</param>
    /// <returns>Die Nummer, oder 0.</returns>
    public static int FromBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return 0;
        }

        Match match = BranchPattern().Match(branch.Trim());
        return match.Success ? ToNumber(match.Groups["id"].Value) : 0;
    }

    /// <summary>Liest die Nummer aus einer Zeile <c>Ticket: &lt;Nummer&gt;</c> der Commit-Meldung.</summary>
    /// <remarks>
    /// Auch <c>TANSS:</c> und <c>TANSS-Ticket:</c> gelten. Steht die Zeile mehrfach, gewinnt die
    /// <b>letzte</b>: Wer seine Meldung im Editor überarbeitet, hängt die berichtigte Zeile
    /// unten an, statt die obere zu suchen.
    /// </remarks>
    /// <param name="message">Die vollständige Commit-Meldung.</param>
    /// <returns>Die Nummer, oder 0.</returns>
    public static int FromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return 0;
        }

        int found = 0;
        foreach (Match match in TrailerPattern().Matches(message))
        {
            int candidate = ToNumber(match.Groups["id"].Value);
            if (candidate > 0)
            {
                found = candidate;
            }
        }

        return found;
    }

    /// <summary>
    /// Bestimmt die Ticketnummer eines Commits nach der Rangfolge Hand → Meldung → Zweig.
    /// </summary>
    /// <param name="commit">Der Commit.</param>
    /// <param name="explicitTicketId">Eine von Hand übergebene Nummer; 0, wenn keine vorliegt.</param>
    /// <param name="fromBranch">Darf der Zweigname befragt werden?</param>
    /// <param name="fromMessage">Darf die Commit-Meldung befragt werden?</param>
    /// <returns>Die Nummer samt Herkunft; niemals <see langword="null"/>.</returns>
    public static TicketReference Resolve(CommitInfo commit, int explicitTicketId = 0,
                                          bool fromBranch = true, bool fromMessage = true)
    {
        ArgumentNullException.ThrowIfNull(commit);

        if (explicitTicketId > 0)
        {
            return new TicketReference(explicitTicketId, TicketSource.Explicit);
        }

        if (fromMessage)
        {
            int fromTrailer = FromMessage(commit.Message);
            if (fromTrailer > 0)
            {
                return new TicketReference(fromTrailer, TicketSource.Trailer);
            }
        }

        if (fromBranch)
        {
            int fromBranchName = FromBranch(commit.Branch);
            if (fromBranchName > 0)
            {
                return new TicketReference(fromBranchName, TicketSource.Branch);
            }
        }

        return TicketReference.None;
    }

    private static int ToNumber(string digits) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
        && value is > 0 and <= Largest
            ? value
            : 0;

    /// <summary>
    /// Ein Zweigname, der auf <c>#</c> und Ziffern endet.
    /// </summary>
    /// <remarks>
    /// Bis zu neun Ziffern, und das Ende des Textes muss folgen. Ohne die Begrenzung fräse sich
    /// das Muster auch durch einen dreissigstelligen Zahlenwurm, nur um am Ende an
    /// <see cref="Largest"/> zu scheitern.
    /// </remarks>
    [GeneratedRegex(@"#(?<id>\d{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex BranchPattern();

    /// <summary>
    /// Eine eigene Zeile <c>Ticket:</c>, <c>TANSS:</c> oder <c>TANSS-Ticket:</c> mit einer Nummer.
    /// </summary>
    /// <remarks>
    /// <c>Multiline</c> lässt <c>^</c> und <c>$</c> auf jede Zeile passen — genau darum geht es:
    /// Die Zeile muss eine Zeile sein und darf kein Satzteil sein. Die Raute vor der Nummer ist
    /// erlaubt, weil sie sich niemand abgewöhnen wird.
    /// </remarks>
    [GeneratedRegex(@"^[ \t]*(?:TANSS-Ticket|TANSS|Ticket)[ \t]*:[ \t]*#?(?<id>\d{1,9})[ \t]*$",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TrailerPattern();
}
