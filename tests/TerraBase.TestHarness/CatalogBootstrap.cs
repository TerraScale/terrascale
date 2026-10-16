using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Replication;

namespace TerraBase.Testing;

/// <summary>
/// The operator's new-cluster bootstrap (docs/terrabase/system-catalog-invariants.md section 2a) for a test's lone machine of
/// a member directory, whose catalog gate otherwise stays closed: nothing to pull from is unknown, not proof of a current
/// catalog. The request is the file <c>fly ssh</c> would write into the data directory.
/// </summary>
public static class CatalogBootstrap
{
    /// <summary>Requests the bootstrap of <paramref name="server"/>'s genesis catalog and waits until its gate is open.</summary>
    public static async Task OpenAsync(TerraBaseTestServer server, string reason = "a test's new cluster")
    {
        ArgumentNullException.ThrowIfNull(server);
        await File.WriteAllTextAsync(Path.Combine(server.DataDirectory, "catalog-bootstrap"), reason);
        await WaitOpenAsync(server);
    }

    /// <summary>Waits until <paramref name="server"/>'s catalog gate is open (it pulled from every machine it needs).</summary>
    public static Task WaitOpenAsync(TerraBaseTestServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var gate = server.Services.GetRequiredService<ICatalogGate>();
        return TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(gate.IsOpen), TimeSpan.FromSeconds(60));
    }
}
