namespace TerraBase.E2E;

/// <summary>
/// Negative controls of a ts-orch run's target (<see cref="Orch.Nodes"/>): a missing or malformed node list is refused
/// before any request.
/// </summary>
public sealed class OrchTests
{
    private static readonly string[] Regions = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];

    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments(null, "names no node for gru, sjc, fra")]
    [Arguments("gru=http://10.0.0.1:18080,sjc=http://10.0.0.2:18080", "names no node for fra")]
    [Arguments("gru=http://10.0.0.1:18080,gru=http://10.0.0.2:18080,sjc=http://10.0.0.2:18080,fra=http://10.0.0.3:18080", "each region once")]
    [Arguments("gru=https://10.0.0.1:18080,sjc=http://10.0.0.2:18080,fra=http://10.0.0.3:18080", "each region once")]
    [Arguments("gru=http://10.0.0.1:18080/v1,sjc=http://10.0.0.2:18080,fra=http://10.0.0.3:18080", "each region once")]
    [Arguments("gru,sjc=http://10.0.0.2:18080,fra=http://10.0.0.3:18080", "each region once")]
    public async Task A_missing_or_malformed_node_list_is_refused_before_any_request(string? nodes, string reason)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(Orch.Nodes(_ => nodes, Regions)));
        await Assert.That(error!.Message).Contains(reason);
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_node_list_names_each_regions_client_listener()
    {
        var nodes = Orch.Nodes(_ => "gru=http://10.0.0.1:18080, sjc=http://10.0.0.2:18080/,fra=http://10.0.0.3:18080", Regions);
        await Assert.That(nodes[Cluster.Sjc]).IsEqualTo(new Uri("http://10.0.0.2:18080/"));
    }
}
