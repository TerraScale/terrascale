using System.Text.Json.Nodes;
using TerraBase.Testing;

namespace TerraBase.E2E.WalFaults;

[Category("tenant-power-loss-host")]
public sealed class TenantPowerLossHost
{
    [Test]
    [Timeout(6 * 3600 * 1000)]
    public async Task Hosts_a_tenant_until_its_process_is_killed(CancellationToken cancellationToken)
    {
        var info = Environment.GetEnvironmentVariable(RegionProcessHost.InfoVariable);
        Skip.When(string.IsNullOrEmpty(info), "the child process of ClientWritePowerLossTests");
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["region"] = Environment.GetEnvironmentVariable(RegionProcessHost.RegionVariable)!;
            settings["node-name"] = Environment.GetEnvironmentVariable(RegionProcessHost.MachineVariable)!;
            settings["data-directory"] = Environment.GetEnvironmentVariable(RegionProcessHost.DataVariable)!;
        });
        await File.WriteAllTextAsync(info + ".partial", new JsonObject { ["internal"] = api.Server.BaseAddress.ToString() }.ToJsonString(), cancellationToken);
        File.Move(info + ".partial", info!, overwrite: true);
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
