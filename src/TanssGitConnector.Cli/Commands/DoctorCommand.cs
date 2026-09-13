using TanssGitConnector.Api;
using TanssGitConnector.Api.Auth;
using TanssGitConnector.Api.Diagnostics;
using TanssGitConnector.Api.Model;
using TanssGitConnector.Cli.Output;
using TanssGitConnector.Git;
using TanssGitConnector.Storage;
using TanssGitConnector.Storage.Queue;
using TanssGitConnector.Storage.Secrets;

namespace TanssGitConnector.Cli.Commands;

/// <summary>
/// Prüft die Einrichtung von oben nach unten: <c>tanss-git doctor</c>.
/// </summary>
/// <remarks>
/// <para><b>Der Rückgabewert ist der Überwachungsvertrag.</b> 0 gesund, 1 verlangt
/// Aufmerksamkeit, 2 gestört. Damit lässt sich das Werkzeug in eine bestehende Überwachung
/// einhängen, ohne Ausgaben zu parsen.</para>
///
/// <para><b>Die Reihenfolge ist Absicht.</b> Sie folgt der Abhängigkeit: Ohne Token kein
/// Zugriff, ohne Zugriff keine Anbindung, ohne Anbindung keine Buchung. Die erste rote Stufe
/// ist die, die behoben gehört — die darunter sind oft nur ihre Folgen.</para>
///
/// <para><b>Eine Prüfung, die selbst scheitert, ist kein Befund.</b> Wo sich etwas nicht klären
/// liess, steht das da, und zwar als Warnung und nicht als Fehler. Der Unterschied zwischen
/// „nicht in Ordnung“ und „nicht feststellbar“ ist die halbe Fehlersuche.</para>
/// </remarks>
internal static class DoctorCommand
{
    /// <summary>Führt alle Prüfungen aus.</summary>
    /// <param name="composition">Die Bausteine dieses Laufs.</param>
    /// <param name="configPath">Der Pfad der Konfiguration, für die Anzeige.</param>
    /// <param name="directory">Das Repository, dessen Hook geprüft wird.</param>
    /// <param name="output">Die Ausgabe.</param>
    /// <param name="ct">Abbruchmarke.</param>
    public static async Task<int> RunAsync(Composition composition, string configPath,
                                           string directory, TextWriter output,
                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine($"tanss-git {CommandLine.HelpText.Version} — Prüfung der Einrichtung");
        output.WriteLine();

        List<CheckResult> checks =
        [
            Configuration(composition, configPath),
            Token(composition),
            await ReachableAsync(composition, ct).ConfigureAwait(false),
        ];

        // Weiter nur, wenn die Instanz ueberhaupt antwortet: Jede folgende Stufe braucht sie,
        // und drei Folgefehler verdecken die eine Ursache.
        if (checks[^1].Level != CheckLevel.Fail)
        {
            checks.Add(await SupportTypeAsync(composition, ct).ConfigureAwait(false));
            checks.Add(await RotationAsync(composition, ct).ConfigureAwait(false));
        }

        checks.Add(await GitAsync(composition, ct).ConfigureAwait(false));
        checks.Add(await HookAsync(composition, directory, ct).ConfigureAwait(false));
        checks.Add(Queue(composition));

        foreach (CheckResult check in checks)
        {
            Report.WriteCheck(output, check);
        }

        output.WriteLine();
        QueueCommand.Prune(composition, output);

        CheckLevel worst = checks.Max(check => check.Level);
        output.WriteLine(worst switch
        {
            CheckLevel.Ok => "Befund: gesund.",
            CheckLevel.Warn => "Befund: läuft, verlangt aber Aufmerksamkeit.",
            _ => "Befund: gestört. In diesem Zustand kommt Arbeitszeit nicht in TANSS an.",
        });

        return Report.ToExitCode(worst);
    }

    private static CheckResult Configuration(Composition composition, string configPath)
    {
        string where = $"Konfiguration {configPath}; Zustand unter {StoragePaths.StateDirectory}.";

        return composition.Config.Tanss.VerifyTls
            ? new CheckResult("Konfiguration", CheckLevel.Ok,
                $"{where} Instanz: {composition.Config.Tanss.BaseUrl}, Mitarbeiter "
                + $"{composition.Config.Tanss.EmployeeId}, Anbindung "
                + $"{composition.Config.Commits.RemoteSupportTypeId}.")
            : new CheckResult("Konfiguration", CheckLevel.Warn,
                $"{where} **Die Zertifikatsprüfung ist abgeschaltet** (tanss.verify_tls = false). "
                + "Damit ist die Verbindung gegen einen Angreifer in der Mitte wertlos. Das ist "
                + "ein Notbehelf für ein selbstsigniertes Zertifikat im eigenen Netz und keine "
                + "Dauerlösung.");
    }

    private static CheckResult Token(Composition composition)
    {
        try
        {
            string token = composition.Tokens.Read();
            TanssTokenClaims claims = TanssAuth.DecodeClaims(token);
            string protection = TokenProtection.DescribeProtection();

            if (claims.ExpiresAt is not { } expiry)
            {
                return new CheckResult("Token", CheckLevel.Warn,
                    $"Ein Token liegt vor ({protection}), es trägt aber keinen Ablauf. Das ist "
                    + "ungewöhnlich; ob es noch gilt, entscheidet allein die Instanz.");
            }

            TimeSpan remaining = expiry - DateTimeOffset.UtcNow;

            if (remaining <= TimeSpan.Zero)
            {
                return new CheckResult("Token", CheckLevel.Fail,
                    $"Das Token ist abgelaufen ({Report.Moment(expiry)}). Ohne gültiges Token "
                    + "wird nichts gebucht. Neu einrichten mit: tanss-git setup");
            }

            CheckLevel level = remaining.TotalDays <= composition.Config.Tanss.RotateBeforeDays
                ? CheckLevel.Warn
                : CheckLevel.Ok;

            return new CheckResult("Token", level,
                $"Gültig noch {Report.Duration(remaining)} (bis {Report.Moment(expiry)}). "
                + $"Schutz: {protection}."
                + (level == CheckLevel.Warn
                    ? " Die Erneuerungsschwelle ist erreicht — sie läuft bei der nächsten "
                      + "Gelegenheit von selbst; von Hand: tanss-git token rotate"
                    : string.Empty));
        }
        catch (StorageException exception)
        {
            return new CheckResult("Token", CheckLevel.Fail, exception.Message);
        }
        catch (TanssAuthException exception)
        {
            return new CheckResult("Token", CheckLevel.Fail, exception.Message);
        }
    }

    private static async Task<CheckResult> ReachableAsync(Composition composition,
                                                          CancellationToken ct)
    {
        try
        {
            IReadOnlyList<Technician> technicians = await composition.Technicians.ListAsync(ct)
                .ConfigureAwait(false);

            int employeeId = composition.Config.Tanss.EmployeeId;
            Technician? own = technicians.FirstOrDefault(person => person.Id == employeeId);

            if (own is not null)
            {
                return new CheckResult("Instanz und Mitarbeiter", CheckLevel.Ok,
                    $"{composition.Config.Tanss.BaseUrl} antwortet. Mitarbeiter {employeeId} ist "
                    + $"„{own.Name}“ — darauf wird jede Buchung geschrieben.");
            }

            return new CheckResult("Instanz und Mitarbeiter", CheckLevel.Fail,
                $"Die Instanz antwortet, aber die Mitarbeiter-ID {employeeId} steht nicht in "
                + $"ihrer Technikerliste ({technicians.Count} Einträge). Gebuchte Zeit hinge "
                + "dann an niemandem und tauchte in keiner Auswertung auf. Die richtige ID "
                + "steht in TANSS unter den Mitarbeitern.");
        }
        catch (TanssException exception)
        {
            return new CheckResult("Instanz und Mitarbeiter", CheckLevel.Fail,
                Redaction.Scrub(exception.Message));
        }
    }

    private static async Task<CheckResult> SupportTypeAsync(Composition composition,
                                                            CancellationToken ct)
    {
        int wanted = composition.Config.Commits.RemoteSupportTypeId;

        try
        {
            IReadOnlyList<RemoteSupportSystem> systems = await composition.RemoteSupports
                .ListSystemsAsync(ct).ConfigureAwait(false);

            RemoteSupportSystem? match = systems.FirstOrDefault(system => system.Id == wanted);

            return match is not null
                ? new CheckResult("Fernwartungs-Anbindung", CheckLevel.Ok,
                    $"Anbindung {wanted} heißt in TANSS „{match.Name}“.")
                : new CheckResult("Fernwartungs-Anbindung", CheckLevel.Fail,
                    $"Die Anbindung {wanted} gibt es in dieser Instanz nicht "
                    + $"({systems.Count} sind angelegt). TANSS weist jede Buchung darauf ab. "
                    + "Die gültigen Kennungen zeigt: tanss-git types");
        }
        catch (TanssException exception)
        {
            return new CheckResult("Fernwartungs-Anbindung", CheckLevel.Warn,
                "Die Anbindungen liessen sich nicht abrufen; ob die eingestellte existiert, ist "
                + "damit ungeklärt. " + Redaction.Scrub(exception.Message));
        }
    }

    private static async Task<CheckResult> RotationAsync(Composition composition,
                                                         CancellationToken ct)
    {
        // Der Trockentest praegt ein Token mit 60 Sekunden Laufzeit. Das ist kein Nulltarif -
        // TANSS fuehrt es in seinem Tokenprotokoll -, aber es ist das einzige ehrliche Mittel,
        // die Frage "darf dieser Mitarbeiter erneuern?" zu beantworten, bevor sie in einem
        // halben Jahr von selbst gestellt wird.
        bool? allowed = await TanssAuth.CanRotateAsync(composition.Client, ct).ConfigureAwait(false);

        return allowed switch
        {
            true => new CheckResult("Token-Erneuerung", CheckLevel.Ok,
                "Der Mitarbeiter darf Token prägen (Recht 480). Die Erneuerung läuft von "
                + "selbst, bevor das Token abläuft."),

            false => new CheckResult("Token-Erneuerung", CheckLevel.Warn,
                "Dem Mitarbeiter fehlt das Recht „Darf API-Tokens für ext. Anbindungen "
                + "erzeugen“ (Recht 480). Das aktuelle Token gilt weiter — aber es lässt sich "
                + "nicht erneuern und stirbt Monate später kommentarlos. Das Recht gehört in "
                + "TANSS vergeben."),

            _ => new CheckResult("Token-Erneuerung", CheckLevel.Warn,
                "Ob erneuert werden darf, liess sich nicht klären. Das ist keine Aussage über "
                + "das Recht — es kann ebenso an der Erreichbarkeit liegen."),
        };
    }

    private static async Task<CheckResult> GitAsync(Composition composition, CancellationToken ct)
    {
        try
        {
            GitResult result = await composition.Git
                .RunAsync(Environment.CurrentDirectory, ["--version"], ct).ConfigureAwait(false);

            return result.Succeeded
                ? new CheckResult("Git", CheckLevel.Ok, result.StandardOutput.Trim())
                : new CheckResult("Git", CheckLevel.Fail,
                    "git ist zwar aufrufbar, meldet aber einen Fehler: " + result.StandardError);
        }
        catch (GitException exception)
        {
            return new CheckResult("Git", CheckLevel.Fail, exception.Message);
        }
    }

    private static async Task<CheckResult> HookAsync(Composition composition, string directory,
                                                     CancellationToken ct)
    {
        string template = Path.Combine(StoragePaths.TemplateDirectory,
                                       HookInstaller.TemplateHooksFolder);
        HookStatus global = HookInstaller.Inspect(template);

        string? configured = await composition.Hooks.ConfiguredTemplateDirectoryAsync(ct)
            .ConfigureAwait(false);

        bool templateActive = global.IsInstalled && configured is { Length: > 0 }
            && HookInstaller.SamePath(configured, StoragePaths.TemplateDirectory);

        string vorlage = templateActive
            ? "Die Vorlage für neue Repositorys ist eingerichtet."
            : "Für neue Repositorys ist keine Vorlage eingerichtet (tanss-git enable --global).";

        try
        {
            HookStatus here = await composition.Hooks.InspectRepositoryAsync(directory, ct)
                .ConfigureAwait(false);

            return here.State switch
            {
                HookState.Ours => new CheckResult("Hook", CheckLevel.Ok,
                    $"In diesem Repository eingerichtet: {here.Path}. {vorlage}"),

                HookState.Foreign => new CheckResult("Hook", CheckLevel.Warn,
                    $"In {here.Path} liegt ein fremder post-commit-Hook. Er bleibt unangetastet; "
                    + "Commits in diesem Repository werden nicht gebucht. Entweder den Aufruf "
                    + "von Hand aufnehmen oder mit „tanss-git enable --force“ ersetzen. "
                    + vorlage),

                HookState.Unreadable => new CheckResult("Hook", CheckLevel.Warn,
                    $"{here.Path} liess sich nicht lesen: {here.Detail} " + vorlage),

                _ => new CheckResult("Hook", CheckLevel.Warn,
                    "In diesem Repository ist kein Hook eingerichtet; Commits hier werden nicht "
                    + "gebucht (tanss-git enable). " + vorlage),
            };
        }
        catch (NotARepositoryException)
        {
            // Kein Repository ist kein Mangel: Der Doktor laeuft oft aus dem Heimatverzeichnis.
            return new CheckResult("Hook", CheckLevel.Ok,
                $"Das Arbeitsverzeichnis gehört zu keinem Repository — hier ist nichts "
                + $"einzurichten. {vorlage}");
        }
    }

    private static CheckResult Queue(Composition composition)
    {
        try
        {
            IReadOnlyList<QueuedCommit> entries = composition.Outbox.All();

            int waiting = entries.Count(entry => entry.State == QueueState.Pending);
            int failed = entries.Count(entry => entry.State == QueueState.Failed);
            int unknown = entries.Count(entry => entry.OutcomeUnknown
                                                 && entry.State == QueueState.Pending);

            if (failed > 0)
            {
                return new CheckResult("Warteschlange", CheckLevel.Fail,
                    $"{failed} aufgegebene(r) Eintrag/Einträge. Darin steht Arbeitszeit, die "
                    + "nicht in TANSS angekommen ist. Erneut versuchen mit: "
                    + "tanss-git queue --flush");
            }

            if (waiting > 0)
            {
                return new CheckResult("Warteschlange", CheckLevel.Warn,
                    $"{waiting} wartende(r) Eintrag/Einträge, davon {unknown} mit ungeklärtem "
                    + "Ausgang. Nichts ist verloren — gesendet wird beim nächsten Commit oder "
                    + "mit „tanss-git queue --flush“; vor jeder Wiederholung eines ungeklärten "
                    + "Eintrags wird geprüft, ob er doch schon in TANSS steht.");
            }

            return new CheckResult("Warteschlange", CheckLevel.Ok,
                $"Nichts liegt an ({entries.Count} Eintrag/Einträge insgesamt, alle erledigt).");
        }
        catch (StorageException exception)
        {
            return new CheckResult("Warteschlange", CheckLevel.Fail, exception.Message);
        }
    }
}
