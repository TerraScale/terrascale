using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Testing;

namespace TerraBase.E2E;

[Category("region-process-host")]
public sealed class RegionProcessHost
{
    internal const string InfoVariable = "TERRABASE_E2E_REGION_HOST";
    internal const string RegionVariable = "TERRABASE_E2E_REGION_NAME";
    internal const string MachineVariable = "TERRABASE_E2E_REGION_MACHINE";
    internal const string DataVariable = "TERRABASE_E2E_REGION_DATA";

    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    [Timeout(6 * 3600 * 1000)]
    public async Task Hosts_one_region_until_its_process_is_killed(CancellationToken cancellationToken)
    {
        var info = Environment.GetEnvironmentVariable(InfoVariable);
        Skip.When(string.IsNullOrEmpty(info), "a node started by the harness in a process of its own");
        var region = Environment.GetEnvironmentVariable(RegionVariable)!;
        var machine = Environment.GetEnvironmentVariable(MachineVariable)!;
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["region"] = region;
            settings["node-name"] = machine;
            settings["data-directory"] = Environment.GetEnvironmentVariable(DataVariable)!;
        }, services =>
        {
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider());

        });

        await File.WriteAllTextAsync(info + ".partial", new JsonObject
        {
            ["internal"] = api.Server.BaseAddress.ToString(),
            ["client"] = api.Server.ClientAddress?.ToString(),
        }.ToJsonString(), cancellationToken);
        File.Move(info + ".partial", info!, overwrite: true);
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
