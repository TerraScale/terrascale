using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Stats;

// The cell is the cell: tag of the NATS user JWT, so the rollups of a member go to the subject of its own cell.
public sealed class StatsNatsCellTests
{
    private const string RollupSubject = "ts.orch.v1.gru.obs.terrabase";

    [Test]
    public async Task A_member_publishes_each_window_on_the_subject_of_its_cell()
    {
        await using var nats = new FakeNats();
        var directory = Directory.CreateTempSubdirectory("terrabase-stats-cell-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "nats.creds");
            await File.WriteAllTextAsync(credentials, CredentialsWith("""{"nats":{"tags":["cell:gru"]}}"""));
            await using var server = await StartAsync(nats, credentials);

            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(nats.Observations.Any(observation => observation.Subject == RollupSubject)),
                TimeSpan.FromSeconds(60));
            var rollup = JsonSerializer.Deserialize<JsonElement>(nats.Observations.First(observation => observation.Subject == RollupSubject).Data);
            await Assert.That(rollup.GetProperty("v").GetInt32()).IsEqualTo(1);
            await Assert.That(rollup.GetProperty("cell").GetString()).IsEqualTo("gru");
            await Assert.That(rollup.GetProperty("region").GetString()).IsEqualTo("test-region");
            await Assert.That(nats.Violations).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_member_without_a_cell_tag_publishes_nothing_and_counts_each_window_as_no_cell()
    {
        await using var nats = new FakeNats();
        var directory = Directory.CreateTempSubdirectory("terrabase-stats-cell-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "nats.creds");
            await File.WriteAllTextAsync(credentials, CredentialsWith("""{"nats":{"tags":["team:gru"]}}"""));
            await using var server = await StartAsync(nats, credentials);

            await TerraBaseTestServer.EventuallyAsync(token => NoCellCountedAsync(server, token), TimeSpan.FromSeconds(60));
            await Assert.That(nats.Observations.Count).IsEqualTo(0);
            await Assert.That(nats.Violations).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<TerraBaseTestServer> StartAsync(FakeNats nats, string credentials) =>
        TerraBaseTestServer.StartAsync(settings =>
        {
            settings["nats-url"] = nats.Url;
            settings["nats-credentials-file"] = credentials;
        });

    // A user JWT whose payload is the given JSON; the signature is never checked against the test server.
    private static string CredentialsWith(string payload) =>
        $"-----BEGIN NATS USER JWT-----\neyJ0.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}.x\n------END NATS USER JWT------\n"
        + "\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n";

    // The counter has no sample until the first window closes.
    private static async Task<bool> NoCellCountedAsync(TerraBaseTestServer server, CancellationToken token)
    {
        try
        {
            return await server.MetricAsync("terrabase_stats_rollups_total", "result=\"no_cell\"", token) > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
