using ApiBench;
using TUnit.Core;

namespace BenchClient.Tests;

public sealed class ParityPeersTests
{
    private const string Rows = "node-gru gru 100.64.1.1\nnode-sjc sjc 100.64.1.2\nnode-fra fra 100.64.1.3\n";

    [Test]
    public async Task DiscoveryKeepsEveryRegionAndDeduplicatesServingNodes()
    {
        var regions = ParityPeers.Parse("  # ts-orch peers\r\n\n" + Rows
            + "node-gru-2\tgru\t100.64.1.4\r\nnode-new\tlhr\t100.64.1.5\n");
        await Assert.That(string.Join(',', regions)).IsEqualTo("fra,gru,lhr,sjc");
    }

    [Test]
    [Arguments("node gru")]
    [Arguments("node gru 100.64.1.1 extra")]
    [Arguments("node gru 100.64.1.1 # inline comments are not rows")]
    [Arguments("Node gru 100.64.1.1")]
    [Arguments("-node gru 100.64.1.1")]
    [Arguments("node- gru 100.64.1.1")]
    [Arguments("node GRU 100.64.1.1")]
    [Arguments("node 1gru 100.64.1.1")]
    [Arguments("node gru- 100.64.1.1")]
    [Arguments("node gru ::1")]
    [Arguments("node gru ::ffff:100.64.1.1")]
    [Arguments("node gru 100.64.1.256")]
    [Arguments("node gru 127.1")]
    [Arguments("node gru 0100.64.1.1")]
    [Arguments("node gru 2130706433")]
    public async Task AnyMalformedRowRejectsTheEntireFileWithoutEchoingIt(string row)
    {
        var error = await Assert.That(() => ParityPeers.Parse(Rows + row)).Throws<InvalidDataException>();
        await Assert.That(error!.Message).IsEqualTo("Parity peers file row 4 is not <node> <region> <ipv4>.");
    }

    [Test]
    [Arguments("gru")]
    [Arguments("sjc")]
    [Arguments("fra")]
    public async Task EachRequiredServingRegionMustBePresent(string missing)
    {
        var rows = string.Join('\n', Rows.Split('\n').Where(row => !row.Contains($" {missing} ", StringComparison.Ordinal)));
        var error = await Assert.That(() => ParityPeers.Parse(rows)).Throws<InvalidDataException>();
        await Assert.That(error!.Message).IsEqualTo($"Parity required serving region {missing} is absent from the peers file.");
    }

    [Test]
    public async Task NamesAcceptTheMaximumLengthAndRejectLongerValues()
    {
        var node = new string('n', 63);
        var region = new string('r', 63);
        await Assert.That(ParityPeers.Parse(Rows + $"{node} {region} 100.64.1.4").Length).IsEqualTo(4);
        await Assert.That(() => ParityPeers.Parse(Rows + $"{node}n lhr 100.64.1.4")).Throws<InvalidDataException>();
        await Assert.That(() => ParityPeers.Parse(Rows + $"node {region}r 100.64.1.4")).Throws<InvalidDataException>();
        await Assert.That(() => ParityPeers.Parse(" # empty peers\n")).Throws<InvalidDataException>();
    }
}
