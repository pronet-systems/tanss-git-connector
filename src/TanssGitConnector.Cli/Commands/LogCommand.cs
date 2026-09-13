using TanssGitConnector.Cli.Output;
using TanssGitConnector.Storage.Logging;

namespace TanssGitConnector.Cli.Commands;

/// <summary>
/// Zeigt das Änderungsprotokoll: <c>tanss-git log [--lines N]</c>.
/// </summary>
/// <remarks>
/// <b>Die jüngste Zeile zuerst.</b> Wer dieses Protokoll aufschlägt, sucht fast immer, was
/// gerade eben geschehen ist — und nicht, was im vorletzten Monat war.
/// </remarks>
internal static class LogCommand
{
    /// <summary>Führt den Befehl aus.</summary>
    public static int Run(Composition composition, int lines, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(output);

        IReadOnlyList<LogEntry> entries = composition.Log.Tail(lines);

        if (entries.Count == 0)
        {
            output.WriteLine("Das Protokoll ist leer: " + composition.Log.Path);
            return ExitCode.Healthy;
        }

        foreach (LogEntry entry in entries)
        {
            string ticket = entry.TicketId is { } id ? $" Ticket {id}" : string.Empty;
            string commit = entry.Commit is { Length: > 0 } sha ? " " + sha : string.Empty;
            string repository = entry.Repository is { Length: > 0 } name ? " " + name : string.Empty;

            output.WriteLine($"{Report.Moment(entry.At)}  {Level(entry.Level),-9} "
                + $"{entry.Event}{commit}{repository}{ticket}");

            foreach (string line in Report.Wrap(entry.Message, 84))
            {
                output.WriteLine("                       " + line);
            }
        }

        return ExitCode.Healthy;
    }

    /// <summary>Die Stufe in derselben Schreibweise wie im Doktor.</summary>
    private static string Level(string level) => level switch
    {
        "error" => "FEHLER",
        "warning" => "WARNUNG",
        "debug" => "debug",
        _ => "info",
    };
}
