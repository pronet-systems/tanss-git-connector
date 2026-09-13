
namespace TanssGitConnector.Storage.Config;

/// <summary>
/// Prüft eine geladene Konfiguration auf Regelverstöße.
/// </summary>
/// <remarks>
/// <para>Die Prüfung sammelt <b>alle</b> Verstöße und wirft erst danach. Wer eine Datei von Hand
/// pflegt, soll sie in einem Durchgang richtigstellen können und nicht nach jedem Aufruf den
/// nächsten Einzelfehler vorgesetzt bekommen.</para>
///
/// <para>Jede Meldung nennt das Feld in der Schreibweise der Datei — also <c>tanss.base_url</c>,
/// nicht <c>Tanss.BaseUrl</c>. Der Benutzer sucht in JSON, nicht im Quelltext.</para>
///
/// <para>Was hier <b>nicht</b> geprüft wird: ob die Instanz erreichbar ist, ob es die
/// Mitarbeiter-ID gibt und ob der Fernwartungstyp in TANSS angelegt wurde. Das kostet
/// Netzzugriffe und gehört in <c>tanss-git doctor</c>, nicht in jeden Commit.</para>
/// </remarks>
public static class ConfigValidator
{
    /// <summary>Kleinster Fernwartungstyp, den TANSS für externe Anbindungen annimmt.</summary>
    public const int MinimumRemoteSupportTypeId = 1000;

    /// <summary>Obergrenze für jede Dauer in Minuten: ein Tag.</summary>
    /// <remarks>
    /// Nicht aus Prinzip, sondern weil eine Fernwartung über mehr als einen Tag in TANSS keine
    /// Arbeitszeit mehr ist, sondern ein Fehler in einer Konfigurationsdatei, den jemand
    /// abtippen musste.
    /// </remarks>
    public const int MaximumMinutes = 24 * 60;

    private const string RequiredSuffix = "/backend";

    private static readonly string[] Levels = ["debug", "info", "warning", "error"];

    /// <summary>Prüft die Konfiguration und wirft bei mindestens einem Verstoß.</summary>
    /// <param name="config">Die geladene Konfiguration.</param>
    /// <param name="origin">Herkunft für die Überschrift, üblicherweise der Dateipfad.</param>
    /// <exception cref="ConfigValidationException">Mindestens eine Regel ist verletzt.</exception>
    public static void Validate(AppConfig config, string? origin = null)
    {
        IReadOnlyList<string> problems = Collect(config);
        if (problems.Count == 0)
        {
            return;
        }

        string headline = string.IsNullOrWhiteSpace(origin)
            ? "Die Konfiguration ist unvollständig oder fehlerhaft:"
            : $"{origin} ist unvollständig oder fehlerhaft:";

        throw new ConfigValidationException(headline, problems);
    }

    /// <summary>Liefert alle Verstöße, ohne zu werfen. Für Prüfbefehle und Oberflächen.</summary>
    public static IReadOnlyList<string> Collect(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<string> problems = [];

        if (config.Version <= 0 || config.Version > AppConfig.CurrentVersion)
        {
            problems.Add($"version: {config.Version} ist unbekannt. Diese Programmversion liest "
                + $"bis Version {AppConfig.CurrentVersion}. Eine neuere Datei mit einer älteren "
                + "Programmversion zu lesen hiesse, Einstellungen zu übergehen, die jemand "
                + "bewusst gesetzt hat.");
        }

        CheckTanss(config.Tanss, problems);
        CheckCommits(config.Commits, problems);
        CheckHook(config.Hook, problems);
        CheckProxy(config.Proxy, problems);
        CheckLogging(config.Logging, problems);

        return problems;
    }

    private static void CheckTanss(TanssSection tanss, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(tanss.BaseUrl))
        {
            problems.Add("tanss.base_url: fehlt. Erwartet wird die Adresse der Instanz "
                + "einschließlich /backend, etwa https://tanss.kunde.de/backend.");
        }
        else
        {
            if (!Uri.TryCreate(tanss.BaseUrl, UriKind.Absolute, out Uri? address)
                || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            {
                problems.Add($"tanss.base_url: „{tanss.BaseUrl}“ ist keine gültige Adresse. Sie "
                    + "muss mit http:// oder https:// beginnen.");
            }

            if (tanss.BaseUrl.EndsWith('/'))
            {
                problems.Add("tanss.base_url: endet mit einem Schrägstrich. Er gehört weg — sonst "
                    + "entstehen Adressen mit doppeltem Schrägstrich, die TANSS abweist.");
            }
            else if (!tanss.BaseUrl.EndsWith(RequiredSuffix, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("tanss.base_url: endet nicht auf /backend. Zeigt die Adresse auf die "
                    + "Weboberfläche, antwortet diese auf jede Anfrage mit HTTP 400 — auch ohne "
                    + "Token, und das kostet bei der Fehlersuche viel Zeit.");
            }
        }

        if (tanss.EmployeeId <= 0)
        {
            problems.Add("tanss.employee_id: fehlt oder ist nicht positiv. Ohne sie hängt keine "
                + "gebuchte Zeit an einem Techniker; sie steht in TANSS unter den Mitarbeitern "
                + "und wird von „tanss-git doctor“ gegengeprüft.");
        }

        if (tanss.TimeoutSeconds is < 1 or > 300)
        {
            problems.Add($"tanss.timeout_seconds: {tanss.TimeoutSeconds} liegt außerhalb von "
                + "1 bis 300.");
        }

        if (tanss.RotateBeforeDays is < 1 or > 364)
        {
            problems.Add($"tanss.rotate_before_days: {tanss.RotateBeforeDays} liegt außerhalb von "
                + "1 bis 364. Der Wert ist eine Restlaufzeit, keine Gesamtlaufzeit.");
        }
    }

    private static void CheckCommits(CommitsSection commits, List<string> problems)
    {
        if (commits.RemoteSupportTypeId < MinimumRemoteSupportTypeId)
        {
            problems.Add($"commits.remote_support_type_id: {commits.RemoteSupportTypeId} ist "
                + $"kleiner als {MinimumRemoteSupportTypeId}. TANSS nimmt über die benutzte "
                + "Route nur externe Anbindungen ab 1000 an; die gültigen Kennungen zeigt "
                + "„tanss-git types“.");
        }

        CheckMinutes("commits.duration_minutes", commits.DurationMinutes, problems);
        CheckMinutes("commits.minimum_minutes", commits.MinimumMinutes, problems);
        CheckMinutes("commits.maximum_minutes", commits.MaximumMinutes, problems);

        if (commits.MinimumMinutes > commits.MaximumMinutes)
        {
            problems.Add($"commits.minimum_minutes ({commits.MinimumMinutes}) ist größer als "
                + $"commits.maximum_minutes ({commits.MaximumMinutes}). In dieser Reihenfolge "
                + "gäbe es keine Dauer, die beide Grenzen einhält.");
        }
    }

    private static void CheckHook(HookSection hook, List<string> problems)
    {
        if (hook.TimeoutSeconds is < 1 or > 120)
        {
            problems.Add($"hook.timeout_seconds: {hook.TimeoutSeconds} liegt außerhalb von 1 bis "
                + "120. Das ist die Zeit, die der Techniker nach jedem Commit wartet.");
        }
    }

    private static void CheckProxy(ProxySection proxy, List<string> problems)
    {
        if (!proxy.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(proxy.Address))
        {
            problems.Add("proxy.address: fehlt, obwohl proxy.enabled gesetzt ist.");
        }

        if (proxy.Port is < 1 or > 65535)
        {
            problems.Add($"proxy.port: {proxy.Port} ist kein gültiger Port.");
        }
    }

    private static void CheckLogging(LoggingSection logging, List<string> problems)
    {
        if (!Levels.Contains(logging.Level, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"logging.level: „{logging.Level}“ ist unbekannt. Erlaubt sind "
                + string.Join(", ", Levels) + ".");
        }

        if (logging.RetentionDays < 0)
        {
            problems.Add($"logging.retention_days: {logging.RetentionDays} ist negativ. 0 heißt "
                + "„beim nächsten Aufräumen fort“.");
        }
    }

    private static void CheckMinutes(string field, int value, List<string> problems)
    {
        if (value is < 1 or > MaximumMinutes)
        {
            problems.Add($"{field}: {value} liegt außerhalb von 1 bis {MaximumMinutes} Minuten.");
        }
    }
}
