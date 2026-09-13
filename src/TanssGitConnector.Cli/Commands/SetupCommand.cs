using System.Globalization;
using System.Text;
using TanssGitConnector.Api;
using TanssGitConnector.Api.Auth;
using TanssGitConnector.Api.Contract;
using TanssGitConnector.Api.Diagnostics;
using TanssGitConnector.Api.Http;
using TanssGitConnector.Api.Model;
using TanssGitConnector.Api.Repository;
using TanssGitConnector.Cli.Output;
using TanssGitConnector.Storage;
using TanssGitConnector.Storage.Config;
using TanssGitConnector.Storage.Secrets;

namespace TanssGitConnector.Cli.Commands;

/// <summary>
/// Die einmalige Einrichtung: <c>tanss-git setup</c>.
/// </summary>
/// <remarks>
/// <para><b>Jeder Schritt wird sofort gegen die echte Instanz geprüft</b>, statt Eingaben nur
/// entgegenzunehmen. Eine Einrichtung, die erst beim ersten Commit auffliegt, kostet den
/// Techniker einen zweiten Anlauf — und in der Zwischenzeit ungebuchte Arbeitszeit.</para>
///
/// <para><b>Die Zugangsdaten werden nicht abgelegt.</b> Sie dienen genau einer Anmeldung; was
/// bleibt, ist das geprägte Arbeitstoken, die eigene Mitarbeiter-ID und die Adresse. Das
/// Anmeldetoken selbst taugt für den Dauerbetrieb nicht: Es lebt Tage statt Monate und gilt auf
/// <c>/api/tanss.x/v1</c> überhaupt nicht.</para>
/// </remarks>
internal static class SetupCommand
{
    /// <summary>Führt die Einrichtung durch.</summary>
    /// <param name="store">Wohin die Konfiguration geschrieben wird.</param>
    /// <param name="input">Die Eingabe.</param>
    /// <param name="output">Die Ausgabe.</param>
    /// <param name="error">Die Fehlerausgabe.</param>
    /// <param name="ct">Abbruchmarke.</param>
    public static async Task<int> RunAsync(ConfigStore store, TextReader input, TextWriter output,
                                           TextWriter error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        Prompt prompt = new(input, output);

        output.WriteLine("Einrichtung des TANSS Git-Connectors");
        output.WriteLine();

        if (store.Exists() && !prompt.YesNo(
                $"Es gibt bereits eine Konfiguration ({store.Path}). Überschreiben?", false))
        {
            output.WriteLine("Abgebrochen. Es wurde nichts geändert.");
            return ExitCode.Healthy;
        }

        string baseUrl = AskBaseUrl(prompt);
        TanssOptions options = new() { BaseUrl = baseUrl, EmployeeId = 0 };

        // --- Anmeldung -------------------------------------------------------------------
        LoginResult login;
        try
        {
            string user = prompt.Line("TANSS-Anmeldename");
            string password = prompt.Secret("Kennwort");
            string factor = prompt.Line("Zweiter Faktor (leer, wenn keiner verlangt wird)", "");

            login = await TanssAuth.LoginAsync(baseUrl, user, password, factor, ct: ct)
                .ConfigureAwait(false);
        }
        catch (TanssException exception)
        {
            error.WriteLine(Redaction.Scrub(exception.Message));
            return ExitCode.Broken;
        }

        output.WriteLine($"Angemeldet als Mitarbeiter {login.EmployeeId}.");
        output.WriteLine();

        options = options with { EmployeeId = login.EmployeeId };

        using TanssClient session = new(options, new OneToken(login.ApiKey));

        // --- Recht 480: darf dieser Mitarbeiter Token praegen? ---------------------------
        bool? mayRotate = await TanssAuth.CanRotateAsync(session, ct).ConfigureAwait(false);

        if (mayRotate == false && !prompt.YesNo(
                "Diesem Mitarbeiter fehlt das Recht „Darf API-Tokens für ext. Anbindungen "
                + "erzeugen“ (Recht 480). Ohne es lässt sich kein Arbeitstoken prägen. "
                + "Trotzdem weiter?", false))
        {
            output.WriteLine("Abgebrochen. Das Recht ist in TANSS zu vergeben.");
            return ExitCode.Broken;
        }

        // --- Arbeitstoken praegen und gegenpruefen ---------------------------------------
        string token;
        try
        {
            token = await TanssAuth.MintAsync(session, TokenCommand.DurationDays,
                $"TANSS Git-Connector ({Environment.MachineName})", forTesting: false, ct)
                .ConfigureAwait(false);
        }
        catch (TanssException exception)
        {
            error.WriteLine("Das Arbeitstoken liess sich nicht prägen: "
                + Redaction.Scrub(exception.Message));
            return ExitCode.Broken;
        }

        ITokenStore tokens = TokenProtection.Default();

        using TanssClient probe = new(options, new OneToken(token));
        TechnicianRepository technicians = new(probe);

        IReadOnlyList<Technician> people;
        try
        {
            people = await technicians.ListAsync(ct).ConfigureAwait(false);
        }
        catch (TanssException exception)
        {
            // Das frisch gepraegte Token wird NICHT abgelegt, wenn es sich nicht bewaehrt hat.
            // Ein unbrauchbares Token abzulegen hiesse, die Einrichtung als gelungen zu
            // melden und den Fehler auf den ersten Commit zu verschieben.
            error.WriteLine("Das geprägte Token wurde abgewiesen: "
                + Redaction.Scrub(exception.Message));
            return ExitCode.Broken;
        }

        Technician? own = people.FirstOrDefault(person => person.Id == login.EmployeeId);
        output.WriteLine(own is not null
            ? $"Token geprägt und geprüft. Gebucht wird auf „{own.Name}“ (ID {own.Id})."
            : $"Token geprägt und geprüft. Die Mitarbeiter-ID {login.EmployeeId} steht "
              + "allerdings nicht in der Technikerliste — das ist vor dem ersten Commit zu klären.");

        tokens.Write(token);
        output.WriteLine("Abgelegt: " + TokenProtection.DescribeProtection());
        output.WriteLine();

        // --- Fernwartungs-Anbindung ------------------------------------------------------
        int typeId;
        try
        {
            typeId = await AskSupportTypeAsync(prompt, new RemoteSupportRepository(probe,
                login.EmployeeId), output, ct).ConfigureAwait(false);
        }
        catch (TanssException exception)
        {
            error.WriteLine("Die Anbindungen liessen sich nicht abrufen: "
                + Redaction.Scrub(exception.Message));
            return ExitCode.Broken;
        }

        if (typeId <= 0)
        {
            error.WriteLine("Ohne Anbindung kann nicht gebucht werden. Sie wird in TANSS unter "
                + "„Externe Fernwartungs-Anbindungen verwalten“ angelegt und muss eine Kennung "
                + "ab 1000 haben.");
            return ExitCode.Broken;
        }

        int minutes = prompt.Number("Wie viele Minuten soll ein Commit buchen?", 15, 1, 24 * 60);

        // --- Schreiben -------------------------------------------------------------------
        AppConfig config = new()
        {
            Tanss = new TanssSection { BaseUrl = baseUrl, EmployeeId = login.EmployeeId },
            Commits = new CommitsSection
            {
                RemoteSupportTypeId = typeId,
                DurationMinutes = minutes,
            },
        };

        try
        {
            store.Save(config);
        }
        catch (StorageException exception)
        {
            error.WriteLine(exception.Message);
            return ExitCode.Broken;
        }

        output.WriteLine();
        output.WriteLine("Geschrieben: " + store.Path);
        output.WriteLine();
        output.WriteLine("Weiter geht es mit:");
        output.WriteLine("  tanss-git enable --global    Haken für künftige Repositorys");
        output.WriteLine("  tanss-git enable             Haken in diesem Repository");
        output.WriteLine("  tanss-git doctor             Einrichtung prüfen");

        return ExitCode.Healthy;
    }

    private static string AskBaseUrl(Prompt prompt)
    {
        while (true)
        {
            string value = prompt.Line("Adresse der TANSS-Instanz (mit /backend)").Trim()
                .TrimEnd('/');

            if (value.Length == 0)
            {
                continue;
            }

            if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                value = "https://" + value;
            }

            if (!value.EndsWith("/backend", StringComparison.OrdinalIgnoreCase))
            {
                // Der haeufigste Einrichtungsfehler ueberhaupt: Zeigt die Adresse auf die
                // Weboberflaeche, antwortet die PHP-Oberflaeche auf JEDE Anfrage mit HTTP 400 -
                // auch ohne Token. Das kostet ohne diesen Hinweis eine halbe Stunde.
                if (prompt.YesNo($"„{value}“ endet nicht auf /backend. Soll „{value}/backend“ "
                                 + "verwendet werden?", true))
                {
                    value += "/backend";
                }
            }

            return value;
        }
    }

    private static async Task<int> AskSupportTypeAsync(Prompt prompt,
                                                       RemoteSupportRepository remoteSupports,
                                                       TextWriter output, CancellationToken ct)
    {
        IReadOnlyList<RemoteSupportSystem> systems = await remoteSupports.ListSystemsAsync(ct)
            .ConfigureAwait(false);

        if (systems.Count == 0)
        {
            return 0;
        }

        output.WriteLine("Auf welche externe Fernwartungs-Anbindung soll gebucht werden?");
        output.WriteLine();

        foreach (RemoteSupportSystem system in systems.OrderBy(item => item.Id))
        {
            output.WriteLine($"  {system.Id,-8} {Report.Ellipsis(system.Name, 56)}");
        }

        output.WriteLine();
        output.WriteLine("Eine eigene Anbindung „Entwicklung“ oder „Git“ ist empfehlenswert: Sie");
        output.WriteLine("trennt Commits in jeder Auswertung von echten Fernwartungen.");

        while (true)
        {
            int chosen = prompt.Number("Kennung", systems[0].Id, 1000, int.MaxValue);

            if (systems.Any(system => system.Id == chosen))
            {
                return chosen;
            }

            output.WriteLine($"Die Kennung {chosen} steht nicht in der Liste. TANSS wiese jede "
                + "Buchung darauf ab.");
        }
    }

    /// <summary>Ein Tokenspeicher für genau einen Lauf — er legt nichts ab.</summary>
    /// <remarks>
    /// Für die beiden Zwischenschritte der Einrichtung: einmal mit dem kurzlebigen
    /// Anmeldetoken, einmal mit dem frisch geprägten, das erst nach bestandener Probe in den
    /// echten Speicher geht.
    /// </remarks>
    private sealed class OneToken : ITokenStore
    {
        private readonly string _token;

        public OneToken(string token) => _token = TokenProtection.Normalize(token);

        public string Read() => _token;

        public void Write(string token) =>
            throw new InvalidOperationException(
                "Dieser Speicher gehört zur Einrichtung und legt nichts ab.");
    }

    /// <summary>
    /// Die Eingabeaufforderungen der Einrichtung.
    /// </summary>
    /// <remarks>
    /// Über <see cref="TextReader"/> und <see cref="TextWriter"/> statt unmittelbar über
    /// <see cref="Console"/>, damit sich der Ablauf ohne Tastatur prüfen lässt. Nur die
    /// verdeckte Eingabe greift auf die Konsole zu — und fällt zurück, wenn keine da ist.
    /// </remarks>
    private sealed class Prompt
    {
        private readonly TextReader _input;
        private readonly TextWriter _output;

        public Prompt(TextReader input, TextWriter output)
        {
            _input = input;
            _output = output;
        }

        /// <summary>Fragt eine Zeile ab.</summary>
        public string Line(string question, string? fallback = null)
        {
            while (true)
            {
                _output.Write(fallback is null ? $"{question}: " : $"{question} [{fallback}]: ");
                _output.Flush();

                string? answer = _input.ReadLine();

                if (answer is null)
                {
                    // Keine Eingabe mehr: Die Einrichtung laesst sich nicht raten.
                    throw new OperationCanceledException(
                        "Die Eingabe ist zu Ende, bevor die Einrichtung abgeschlossen war.");
                }

                answer = answer.Trim();

                if (answer.Length > 0)
                {
                    return answer;
                }

                if (fallback is not null)
                {
                    return fallback;
                }
            }
        }

        /// <summary>Fragt ein Kennwort ab — ohne es anzuzeigen, wo das geht.</summary>
        /// <remarks>
        /// Ohne Konsole (umgeleitete Eingabe, Prüflauf) wird gewöhnlich gelesen. Das ist kein
        /// Sicherheitsmangel, sondern die einzige Möglichkeit: Wo es keine Konsole gibt, gibt es
        /// auch niemanden, dem man etwas verbergen könnte.
        /// </remarks>
        public string Secret(string question)
        {
            _output.Write(question + ": ");
            _output.Flush();

            if (Console.IsInputRedirected)
            {
                return _input.ReadLine()?.Trim() ?? string.Empty;
            }

            StringBuilder secret = new();

            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    _output.WriteLine();
                    return secret.ToString();
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (secret.Length > 0)
                    {
                        secret.Length--;
                    }

                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    _ = secret.Append(key.KeyChar);
                }
            }
        }

        /// <summary>Fragt eine Ja-Nein-Frage.</summary>
        public bool YesNo(string question, bool fallback)
        {
            string answer = Line(question + (fallback ? " (J/n)" : " (j/N)"), fallback ? "j" : "n");

            return answer.StartsWith('j') || answer.StartsWith('J')
                || answer.StartsWith('y') || answer.StartsWith('Y');
        }

        /// <summary>Fragt eine Zahl in einem Bereich ab.</summary>
        public int Number(string question, int fallback, int lowest, int highest)
        {
            while (true)
            {
                string answer = Line(question, fallback.ToString(CultureInfo.InvariantCulture));

                if (int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture,
                                 out int value) && value >= lowest && value <= highest)
                {
                    return value;
                }

                _output.WriteLine($"Erwartet wird eine ganze Zahl zwischen {lowest} und {highest}.");
            }
        }
    }
}
