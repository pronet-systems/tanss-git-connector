using System.Reflection;

namespace TanssGitConnector.Cli.CommandLine;

/// <summary>Die Hilfe und die Meldung bei fehlender Einrichtung.</summary>
/// <remarks>
/// Beides steht hier und nicht verstreut in den Befehlen: Es sind die zwei Texte, die ein
/// Benutzer am häufigsten zu sehen bekommt, und sie sollen zusammenpassen.
/// </remarks>
public static class HelpText
{
    /// <summary>Die Fassung des Programms, so wie sie beim Übersetzen eingetragen wurde.</summary>
    public static string Version =>
        typeof(HelpText).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? "unbekannt";

    /// <summary>Schreibt die Hilfe.</summary>
    public static void Write(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine($"tanss-git {Version} — bucht Git-Commits als Fernwartungen in TANSS.");
        output.WriteLine();
        output.WriteLine("Aufruf: tanss-git <Befehl> [Optionen]");
        output.WriteLine();
        output.WriteLine("Einrichten");
        output.WriteLine("  setup                     Anmeldung, Token, Mitarbeiter, Anbindung — einmalig");
        output.WriteLine("  enable [--global]         den post-commit-Haken einrichten");
        output.WriteLine("         [--force]          einen fremden Haken ersetzen (der alte wird gesichert)");
        output.WriteLine("  disable [--global]        den Haken wieder entfernen");
        output.WriteLine("  status                    zeigt, was eingerichtet ist");
        output.WriteLine("  types                     die externen Fernwartungs-Anbindungen der Instanz");
        output.WriteLine();
        output.WriteLine("Betrieb");
        output.WriteLine("  doctor                    prüft Erreichbarkeit, Token, Rechte, Haken, Warteschlange");
        output.WriteLine("  queue [--flush]           Warteschlange anzeigen; --flush sendet die fälligen Einträge");
        output.WriteLine("  log [--lines N]           das Änderungsprotokoll, jüngste Zeile zuerst");
        output.WriteLine("  token status|rotate       Restlaufzeit anzeigen, Token erneuern");
        output.WriteLine();
        output.WriteLine("Buchen");
        output.WriteLine("  book <Commit> [--ticket N] einen Commit nachträglich buchen");
        output.WriteLine("       [--dry-run]           zeigt nur, was gebucht würde");
        output.WriteLine("  hook                      der Aufruf aus dem Haken — endet immer mit 0");
        output.WriteLine();
        output.WriteLine("Überall gültig");
        output.WriteLine("  --repository <Pfad>, -C   in diesem Repository arbeiten (Vorgabe: Arbeitsverzeichnis)");
        output.WriteLine("  --quiet                   nichts ausgeben, wenn alles gutgegangen ist");
        output.WriteLine("  --help, --version");
        output.WriteLine();
        output.WriteLine("Rückgabewerte: 0 gesund, 1 Warnung, 2 gestört, 64 Aufruffehler.");
        output.WriteLine("„hook“ endet immer mit 0 — ein geschriebener Commit soll nie als");
        output.WriteLine("misslungen erscheinen, nur weil das Buchen nicht ging.");
        output.WriteLine();
        output.WriteLine("Ticketbezug: Der Zweig endet auf #<Nummer> (feature/xy#5000), oder die");
        output.WriteLine("Commit-Meldung trägt eine eigene Zeile „Ticket: 5000“.");
    }

    /// <summary>Die Meldung, wenn keine Konfiguration vorliegt.</summary>
    /// <remarks>
    /// Sie nennt den Pfad, den Grund und den Befehl, der hilft. Ein blosses „nicht eingerichtet“
    /// liesse offen, wo gesucht wurde — und genau das ist die erste Frage.
    /// </remarks>
    public static void WriteMissingConfiguration(TextWriter output, string path, string reason)
    {
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine("Dieses Werkzeug ist auf diesem Rechner noch nicht eingerichtet.");
        output.WriteLine();
        output.WriteLine("  Erwartet wird:  " + path);
        output.WriteLine("  Grund:          " + reason);
        output.WriteLine();
        output.WriteLine("Einrichten mit:   tanss-git setup");
        output.WriteLine("Von Hand: config.example.json neben dem Programm als Vorlage nehmen.");
    }
}
