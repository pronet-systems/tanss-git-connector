using TanssGitConnector.Api;
using TanssGitConnector.Api.Auth;
using TanssGitConnector.Api.Contract;
using TanssGitConnector.Api.Diagnostics;
using TanssGitConnector.Cli.Output;
using TanssGitConnector.Storage;
using TanssGitConnector.Storage.Secrets;

namespace TanssGitConnector.Cli.Commands;

/// <summary>
/// Tokenstand und Tokenwechsel: <c>tanss-git token status|rotate</c>.
/// </summary>
/// <remarks>
/// <para><b>Das Token ist ein Ausweis.</b> Es erlaubt, im Namen des Technikers in TANSS zu
/// handeln. Es liegt geschützt auf der Platte, hat eine begrenzte Laufzeit und wird erneuert,
/// bevor sie abläuft.</para>
///
/// <para><b>TANSS 10.10.0 kennt keinen Widerruf.</b> Ein einmal ausgestelltes Token bleibt bis
/// zu seinem Ablauf gültig. Deshalb ist die Laufzeit hier mit <see cref="DurationDays"/> kürzer
/// als die von TANSS angebotenen 365 Tage — und deshalb prägt dieses Werkzeug nie „zur
/// Sicherheit“ ein zweites.</para>
/// </remarks>
internal static class TokenCommand
{
    /// <summary>
    /// Die Laufzeit eines neu geprägten Tokens in Tagen.
    /// </summary>
    /// <remarks>
    /// 180 statt der möglichen 365: Ein Token, das sich nicht widerrufen lässt, sollte nicht
    /// länger gelten als nötig. Ein halbes Jahr überbrückt jede Urlaubs- und Krankheitslücke,
    /// und die Erneuerung läuft von selbst.
    /// </remarks>
    public const int DurationDays = 180;

    /// <summary>Führt den Befehl aus.</summary>
    /// <param name="composition">Die Bausteine dieses Laufs.</param>
    /// <param name="subCommand"><c>status</c> oder <c>rotate</c>.</param>
    /// <param name="output">Die gewöhnliche Ausgabe.</param>
    /// <param name="error">Die Fehlerausgabe.</param>
    /// <param name="ct">Abbruchmarke.</param>
    public static async Task<int> RunAsync(Composition composition, string subCommand,
                                           TextWriter output, TextWriter error,
                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        return subCommand switch
        {
            "rotate" => await RotateAsync(composition, output, error, ct).ConfigureAwait(false),
            _ => Status(composition, output, error),
        };
    }

    private static int Status(Composition composition, TextWriter output, TextWriter error)
    {
        try
        {
            TanssTokenClaims claims = TanssAuth.DecodeClaims(composition.Tokens.Read());

            output.WriteLine("Schutz          " + TokenProtection.DescribeProtection());
            output.WriteLine("Ausgestellt     " + (claims.IssuedAt is { } issued
                ? Report.Moment(issued) : "unbekannt"));

            if (claims.ExpiresAt is { } expiry)
            {
                TimeSpan remaining = expiry - DateTimeOffset.UtcNow;
                output.WriteLine("Läuft ab        " + Report.Moment(expiry));
                output.WriteLine("Restlaufzeit    " + Report.Duration(remaining));
                output.WriteLine("Erneuerung      ab einer Restlaufzeit von "
                    + $"{composition.Config.Tanss.RotateBeforeDays} Tagen");

                return remaining <= TimeSpan.Zero ? ExitCode.Broken
                    : remaining.TotalDays <= composition.Config.Tanss.RotateBeforeDays
                        ? ExitCode.Warning
                        : ExitCode.Healthy;
            }

            output.WriteLine("Läuft ab        keine Angabe im Token");
            return ExitCode.Warning;
        }
        catch (StorageException exception)
        {
            error.WriteLine(exception.Message);
            return ExitCode.Broken;
        }
        catch (TanssAuthException exception)
        {
            error.WriteLine(exception.Message);
            return ExitCode.Broken;
        }
    }

    /// <summary>
    /// Erneuert das Token — und übernimmt es erst nach bestandener Probe.
    /// </summary>
    /// <remarks>
    /// <b>Die Gegenprobe ist keine Höflichkeit.</b> Würde erst geschrieben und dann geprüft,
    /// stünde das Werkzeug bei einem unbrauchbaren neuen Token ohne jeden Zugang da. Solange das
    /// alte gültig ist, ist der schlechtestmögliche Ausgang: alles bleibt, wie es war.
    /// </remarks>
    private static async Task<int> RotateAsync(Composition composition, TextWriter output,
                                               TextWriter error, CancellationToken ct)
    {
        try
        {
            TokenRotationResult result = await TanssAuth.RotateIfNeededAsync(
                composition.Client,
                composition.Tokens,
                verify: async (candidate, token) =>
                {
                    // Ein echter Aufruf mit dem neuen Token, und zwar ein lesender: Die
                    // Technikerliste aendert nichts und beweist trotzdem, dass das Token auf
                    // beiden Praefixen angenommen wird.
                    ITanssClient probe = composition.CreateClientWith(candidate);
                    var technicians = new Api.Repository.TechnicianRepository(probe);
                    return (await technicians.ListAsync(token).ConfigureAwait(false)).Count > 0;
                },
                beforeDays: composition.Config.Tanss.RotateBeforeDays,
                durationDays: DurationDays,
                info: "TANSS Git-Connector",
                ct: ct).ConfigureAwait(false);

            output.WriteLine(result.Reason);

            if (result.NewExpiry is { } expiry)
            {
                output.WriteLine("Neues Token gültig bis " + Report.Moment(expiry));
            }

            if (result.Error is { Length: > 0 } problem)
            {
                error.WriteLine(problem);
                composition.Log.Error("token.rotate", problem);
                return ExitCode.Warning;
            }

            composition.Log.Info("token.rotate", result.Reason);
            return ExitCode.Healthy;
        }
        catch (TanssException exception)
        {
            error.WriteLine(Redaction.Scrub(exception.Message));
            return ExitCode.Broken;
        }
        catch (StorageException exception)
        {
            error.WriteLine(exception.Message);
            return ExitCode.Broken;
        }
    }
}
