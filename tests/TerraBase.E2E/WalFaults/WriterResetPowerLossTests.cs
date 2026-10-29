using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.E2E.WalFaults;

/// <summary>
/// A write whose commit synced is answered as written even when resetting its writer session fails afterwards, and that
/// answer is true: the write survives a power loss of the node. The next write, on a fresh session, survives too.
/// </summary>
public sealed class WriterResetPowerLossTests
{
    private const string Arm = "fail-writer-reset";

    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill | Faults.FileTampering)]
    public async Task A_write_answered_after_its_writer_reset_failed_survives_a_power_loss()
    {
        Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
        await using var node = await PowerLossNode.StartAsync(nameof(WriterResetFaultHost));
        var createdDatabase = await node.CreateDatabaseAsync(EntityIds.New().ToString());
        var project = createdDatabase.Project;
        var path = $"/v1/projects/{project}/databases/canary";
        await Cluster.CreateCollectionAsync(new(StringComparer.Ordinal) { [Cluster.Gru] = node.Client }, path, "items");
        string[] ids = [EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString()];

        await Docs.PutAsync(node.Client, Document(path, ids[0]), new JsonObject { ["n"] = 0 });
        await System.IO.File.WriteAllTextAsync(node.PathOf(Arm), string.Empty);
        using (var answered = await node.Client.PutAsJsonAsync(Document(path, ids[1]), new JsonObject { ["n"] = 1 }))
        {
            await Assert.That(answered.StatusCode).IsEqualTo(HttpStatusCode.Created).Because("the write committed and synced before its reset failed");
        }

        System.IO.File.Delete(node.PathOf(Arm));
        await Docs.PutAsync(node.Client, Document(path, ids[2]), new JsonObject { ["n"] = 2 });

        await node.PowerLossAsync();
        await node.StartAgainAsync();
        foreach (var (index, id) in ids.Index())
        {
            var document = await Docs.ReadAsync(node.Client, Document(path, id));
            await Assert.That(document?["n"]?.GetValue<int>()).IsEqualTo(index).Because($"write {index + 1} was answered before the power loss");
        }
    }

    private static Uri Document(string path, string id) => new($"{path}/collections/items/documents/by-key?id={id}", UriKind.Relative);
}

/// <summary>
/// The node of <see cref="WriterResetPowerLossTests"/>: as <see cref="RegionProcessHost"/>, alone (no member directory),
/// and every client database writer's reset fails while the root's arm file exists (the lifecycle checkpoint
/// <c>writer.reset</c>, a no-op in production).
/// </summary>
public sealed class WriterResetFaultHost
{
    [Test]
    [Timeout(6 * 3600 * 1000)]
    public async Task Hosts_one_node_whose_writer_resets_fail_while_armed(CancellationToken cancellationToken)
    {
        var info = Environment.GetEnvironmentVariable(RegionProcessHost.InfoVariable);
        Skip.When(string.IsNullOrEmpty(info), "the node of WriterResetPowerLossTests: started by it in a process of its own");
        var arm = Path.Combine(Path.GetDirectoryName(info)!, "fail-writer-reset");
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["region"] = Environment.GetEnvironmentVariable(RegionProcessHost.RegionVariable)!;
            settings["node-name"] = Environment.GetEnvironmentVariable(RegionProcessHost.MachineVariable)!;
            settings["data-directory"] = Environment.GetEnvironmentVariable(RegionProcessHost.DataVariable)!;
        }, services =>
        {
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider());
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
            {
                if (step == "writer.reset" && System.IO.File.Exists(arm))
                {
                    throw new IOException("a test makes the writer's reset fail");
                }
            });
        });

        await System.IO.File.WriteAllTextAsync(info + ".partial", new JsonObject { ["internal"] = api.Server.BaseAddress.ToString() }.ToJsonString(), cancellationToken);
        System.IO.File.Move(info + ".partial", info!, overwrite: true);
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
