using System.Globalization;

namespace TanssGitConnector.Cli.CommandLine;

/// <summary>Die Befehle des Werkzeugs.</summary>
public enum CliCommand
{
    /// <summary>Kein Befehl erkannt.</summary>
    None,

    /// <summary>Einrichtung: Anmeldung, Token, Mitarbeiter, Anbindung.</summary>
    Setup,

    /// <summary>Erreichbarkeit, Token, Rechte, Haken und Warteschlange prüfen.</summary>
    Doctor,

    /// <summary>Den Haken einrichten.</summary>
    Enable,

    /// <summary>Den Haken entfernen.</summary>
    Disable,

    /// <summary>Zeigen, was eingerichtet ist.</summary>
    Status,

    /// <summary>Der Aufruf aus dem <c>post-commit</c>-Haken.</summary>
    Hook,

    /// <summary>Einen bestimmten Commit nachträglich buchen.</summary>
    Book,

    /// <summary>Warteschlange anzeigen und senden.</summary>
    Queue,

    /// <summary>Tokenstand und Tokenwechsel.</summary>
    Token,

    /// <summary>Die externen Fernwartungs-Anbindungen der Instanz auflisten.</summary>
    Types,

    /// <summary>Das Änderungsprotokoll anzeigen.</summary>
    Log,
}

/// <summary>
/// Die ausgewertete Befehlszeile.
/// </summary>
/// <remarks>
/// <para><b>Von Hand ausgewertet, ohne Paket.</b> Bei elf Befehlen und einer Handvoll Schalter
/// trägt ein Auswertungspaket nichts bei, was diese Datei nicht in wenigen Zeilen leistet — und
/// es brächte eine eigene, englischsprachige Hilfe- und Fehlerausgabe mit. Deutsche
/// Benutzertexte sind hier Hausregel und keine Kür.</para>
/// <para>Die Auswertung wirft <b>nie</b>. Ein Aufruffehler ist ein Ergebnis
/// (<see cref="Error"/>), kein Ausnahmefall: Er soll denselben Weg durch die Ausgabe nehmen wie
/// jede andere Meldung und mit <see cref="ExitCode.Usage"/> enden.</para>
/// </remarks>
public sealed record CliArgs
{
    /// <summary>Die Befehle, wie sie auf der Befehlszeile heißen.</summary>
    private static readonly Dictionary<string, CliCommand> Commands = new(StringComparer.Ordinal)
    {
        ["setup"] = CliCommand.Setup,
        ["doctor"] = CliCommand.Doctor,
        ["enable"] = CliCommand.Enable,
        ["disable"] = CliCommand.Disable,
        ["status"] = CliCommand.Status,
        ["hook"] = CliCommand.Hook,
        ["book"] = CliCommand.Book,
        ["queue"] = CliCommand.Queue,
        ["token"] = CliCommand.Token,
        ["types"] = CliCommand.Types,
        ["log"] = CliCommand.Log,
    };

    /// <summary>Der erkannte Befehl.</summary>
    public CliCommand Command { get; init; }

    /// <summary>Der Unterbefehl, derzeit nur bei <see cref="CliCommand.Token"/>.</summary>
    public string? SubCommand { get; init; }

    /// <summary>Das Repository, in dem gearbeitet wird; ohne Angabe das Arbeitsverzeichnis.</summary>
    public string? Repository { get; init; }

    /// <summary>Die Fassung, die gebucht werden soll — bei <c>book</c>.</summary>
    public string? Revision { get; init; }

    /// <summary>Eine von Hand angegebene Ticketnummer. Sie schlägt Zweigname und Meldung.</summary>
    public int? TicketId { get; init; }

    /// <summary>Zeigen, was geschähe, ohne etwas einzureihen oder zu senden.</summary>
    public bool DryRun { get; init; }

    /// <summary><c>queue --flush</c>: fällige Einträge senden.</summary>
    public bool Flush { get; init; }

    /// <summary><c>enable --global</c>: als Vorlage für künftige Repositorys.</summary>
    public bool Global { get; init; }

    /// <summary><c>enable --force</c>: einen fremden Haken ersetzen, nach Sicherung.</summary>
    public bool Force { get; init; }

    /// <summary>Nichts ausgeben, wenn alles gutgegangen ist.</summary>
    public bool Quiet { get; init; }

    /// <summary><c>log --lines</c>: wie viele Zeilen.</summary>
    public int Lines { get; init; } = 30;

    /// <summary>Hilfe wurde angefordert.</summary>
    public bool HelpRequested { get; init; }

    /// <summary>Die Fassung wurde angefordert.</summary>
    public bool VersionRequested { get; init; }

    /// <summary>
    /// Der Aufruffehler in deutscher Prosa, sonst <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Der Text sagt, was nicht ging und was stattdessen erwartet wird. Ein blosses „unbekannte
    /// Option“ zwänge den Benutzer, die Hilfe abzutippen, bis er die richtige Schreibweise trifft.
    /// </remarks>
    public string? Error { get; init; }

    /// <summary>Wertet die Befehlszeile aus.</summary>
    /// <param name="args">Die Argumente ohne den Programmnamen.</param>
    public static CliArgs Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return new CliArgs { Error = "Es wurde kein Befehl angegeben. " + Overview };
        }

        string head = args[0];

        if (IsHelp(head))
        {
            return new CliArgs { HelpRequested = true };
        }

        if (string.Equals(head, "--version", StringComparison.Ordinal))
        {
            return new CliArgs { VersionRequested = true };
        }

        if (!Commands.TryGetValue(head, out CliCommand command))
        {
            return new CliArgs { Error = $"Unbekannter Befehl „{head}“. " + Overview };
        }

        CliArgs result = new() { Command = command };

        for (int index = 1; index < args.Count; index++)
        {
            string token = args[index];

            if (IsHelp(token))
            {
                return result with { HelpRequested = true };
            }

            switch (token)
            {
                case "--dry-run" when command is CliCommand.Hook or CliCommand.Book:
                    result = result with { DryRun = true };
                    continue;

                case "--flush" when command == CliCommand.Queue:
                    result = result with { Flush = true };
                    continue;

                case "--global" when command is CliCommand.Enable or CliCommand.Disable:
                    result = result with { Global = true };
                    continue;

                case "--force" when command == CliCommand.Enable:
                    result = result with { Force = true };
                    continue;

                case "--quiet":
                    result = result with { Quiet = true };
                    continue;

                case "--repository":
                case "-C":
                    if (Next(args, ref index) is not { } repository)
                    {
                        return result with
                        {
                            Error = "„--repository“ erwartet ein Verzeichnis, etwa "
                                + "„--repository /pfad/zum/projekt“. Ohne Angabe gilt das "
                                + "Arbeitsverzeichnis.",
                        };
                    }

                    result = result with { Repository = repository };
                    continue;

                case "--ticket" when command is CliCommand.Book or CliCommand.Hook:
                    if (Next(args, ref index) is not { } ticket)
                    {
                        return result with
                        {
                            Error = "„--ticket“ erwartet eine Ticketnummer, etwa „--ticket 5000“.",
                        };
                    }

                    if (!int.TryParse(ticket, NumberStyles.None, CultureInfo.InvariantCulture,
                                      out int ticketId) || ticketId <= 0)
                    {
                        return result with
                        {
                            Error = $"„--ticket {ticket}“ ist keine Ticketnummer. Erwartet wird "
                                + "eine positive ganze Zahl.",
                        };
                    }

                    result = result with { TicketId = ticketId };
                    continue;

                case "--lines" when command == CliCommand.Log:
                    if (Next(args, ref index) is not { } lines
                        || !int.TryParse(lines, NumberStyles.None, CultureInfo.InvariantCulture,
                                         out int count) || count <= 0)
                    {
                        return result with
                        {
                            Error = "„--lines“ erwartet eine positive ganze Zahl, etwa "
                                + "„--lines 50“.",
                        };
                    }

                    result = result with { Lines = count };
                    continue;

                default:
                    break;
            }

            if (token.StartsWith('-'))
            {
                return result with
                {
                    Error = $"Unbekannte Option „{token}“ für den Befehl „{head}“. Was dieser "
                        + $"Befehl kennt, zeigt „tanss-git {head} --help“.",
                };
            }

            // Ein freistehendes Wort: je nach Befehl der Unterbefehl oder die Fassung.
            if (command == CliCommand.Token && result.SubCommand is null)
            {
                if (token is not ("status" or "rotate"))
                {
                    return result with
                    {
                        Error = $"Unbekannter Unterbefehl „{token}“. „tanss-git token“ kennt "
                            + "„status“ und „rotate“.",
                    };
                }

                result = result with { SubCommand = token };
                continue;
            }

            if (command == CliCommand.Book && result.Revision is null)
            {
                result = result with { Revision = token };
                continue;
            }

            return result with
            {
                Error = $"Mit „{token}“ kann der Befehl „{head}“ nichts anfangen. Die Übersicht "
                    + "zeigt „tanss-git --help“.",
            };
        }

        return result;
    }

    /// <summary>Der Satz, der in jeder Aufrufmeldung die gültigen Befehle nennt.</summary>
    private static string Overview =>
        "Erwartet wird einer von: " + string.Join(", ", Commands.Keys)
        + ". Die vollständige Übersicht zeigt „tanss-git --help“.";

    private static bool IsHelp(string token) =>
        token is "--help" or "-h" or "help" or "-?" or "/?";

    /// <summary>Holt den Wert zu einer Option und rückt den Zeiger weiter.</summary>
    private static string? Next(IReadOnlyList<string> args, ref int index)
    {
        if (index + 1 >= args.Count)
        {
            return null;
        }

        string value = args[index + 1];

        // Ein Wert, der selbst wie eine Option aussieht, ist fast immer eine vergessene Angabe.
        // Ihn stillschweigend zu nehmen ergaebe ein Repository namens "--force".
        if (value.StartsWith('-'))
        {
            return null;
        }

        index++;
        return value;
    }
}
