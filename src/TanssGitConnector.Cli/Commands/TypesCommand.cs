using TanssGitConnector.Api;
using TanssGitConnector.Api.Diagnostics;
using TanssGitConnector.Api.Model;
using TanssGitConnector.Cli.Output;

namespace TanssGitConnector.Cli.Commands;

/// <summary>
/// Listet die externen Fernwartungs-Anbindungen der Instanz: <c>tanss-git types</c>.
/// </summary>
/// <remarks>
/// Der Befehl, den man beim Einrichten braucht: <c>commits.remote_support_type_id</c> muss eine
/// dieser Kennungen sein. Eine erfundene Zahl weist TANSS bei jeder Buchung ab — mit
/// <c>TYPE_DOESNT_EXIST</c>, und das steht dann in der Warteschlange statt in der Abrechnung.
/// </remarks>
internal static class TypesCommand
{
    /// <summary>Führt den Befehl aus.</summary>
    public static async Task<int> RunAsync(Composition composition, TextWriter output,
                                           TextWriter error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        IReadOnlyList<RemoteSupportSystem> systems;
        try
        {
            systems = await composition.RemoteSupports.ListSystemsAsync(ct).ConfigureAwait(false);
        }
        catch (TanssException exception)
        {
            error.WriteLine(Redaction.Scrub(exception.Message));
            return ExitCode.Broken;
        }

        if (systems.Count == 0)
        {
            output.WriteLine("Diese Instanz führt keine externe Fernwartungs-Anbindung.");
            output.WriteLine();
            output.WriteLine("Sie wird in TANSS unter „Externe Fernwartungs-Anbindungen verwalten“");
            output.WriteLine("angelegt und muss eine Kennung ab 1000 haben. Eine eigene Anbindung");
            output.WriteLine("namens „Entwicklung“ oder „Git“ trennt die Commits in jeder Auswertung");
            output.WriteLine("von echten Fernwartungen.");
            return ExitCode.Warning;
        }

        int active = composition.Config.Commits.RemoteSupportTypeId;

        output.WriteLine("Kennung  Leistungstyp  Name");

        foreach (RemoteSupportSystem system in systems.OrderBy(item => item.Id))
        {
            string marker = system.Id == active ? " ←  eingestellt" : string.Empty;
            output.WriteLine($"{system.Id,-8} {system.SupportTypeId,-13} "
                + Report.Ellipsis(system.Name, 48) + marker);
        }

        if (systems.All(system => system.Id != active))
        {
            output.WriteLine();
            output.WriteLine($"Achtung: Eingestellt ist {active} — diese Kennung steht nicht in der");
            output.WriteLine("Liste. TANSS weist jede Buchung darauf ab.");
            return ExitCode.Warning;
        }

        return ExitCode.Healthy;
    }
}
