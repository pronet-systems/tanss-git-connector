using TanssGitConnector.Api.Contract;
using TanssGitConnector.Api.Model;

namespace TanssGitConnector.Api.Repository;

/// <summary>Die Techniker der Instanz.</summary>
/// <remarks>
/// Liegt auf <c>/api/tanss.x/v1</c> und braucht deshalb kein <c>loggedInUserId</c>. Dient der
/// Einrichtung: hier findet der Benutzer seine eigene Mitarbeiter-ID, die anschließend die
/// gesamte Attribution der gebuchten Commits trägt.
/// </remarks>
public sealed class TechnicianRepository : ITechnicianRepository
{
    private readonly ITanssClient _client;

    /// <summary>Baut das Repository.</summary>
    /// <param name="client">Der HTTP-Zugang.</param>
    public TechnicianRepository(ITanssClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Technician>> ListAsync(CancellationToken ct = default)
    {
        List<Technician>? technicians = await _client
            .GetAsync<List<Technician>>(TanssRoutes.Technicians, ct: ct).ConfigureAwait(false);

        return technicians ?? [];
    }
}
