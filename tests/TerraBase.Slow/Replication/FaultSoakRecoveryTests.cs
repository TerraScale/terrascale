using System.Net;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.Slow.Replication;

[Category("replication-fault-soak")]
public sealed class FaultSoakRecoveryTests
{
    [Test]
    [Timeout(300_000)]
    public Task Local_ack_survives_restart(CancellationToken cancellationToken) => CheckAsync(false, cancellationToken);

    [Test]
    [Timeout(300_000)]
    public Task Remote_ack_with_two_healthy_peers_survives_restart(CancellationToken cancellationToken) => CheckAsync(true, cancellationToken);

    private static async Task CheckAsync(bool remote, CancellationToken cancellationToken)
    {
        await using var cluster = remote
            ? await FaultSoakCluster.StartAsync(0, cancellationToken)
            : await FaultSoakCluster.StartLocalAsync(0, cancellationToken);
        var id = EntityIds.New().ToString();
        const string expected = "restart-liveness";
        using (var answer = await cluster["gru"].PutAsJsonAsync(cluster.Document(id), new { value = expected }, cancellationToken))
        {
            await FaultSoakChecks.SuccessAsync(answer, cancellationToken);
        }

        if (remote)
        {
            await FaultSoakChecks.UntilAsync(async () =>
                (await Task.WhenAll(cluster.Regions.Select(region =>
                    FaultSoakChecks.ReadAsync(cluster[region], cluster.Document(id), cancellationToken))))
                .All(copy => copy?["value"]?.GetValue<string>() == expected),
                "all three replicas retain the acknowledged document before restart", cancellationToken);
        }

        // Without an intact peer, serving recovery is a qualification beyond the documented refusal contract.
        // No WAL truncation: isolate restart serving from the simulated power-loss model.
        await cluster.KillAsync("gru");
        var stored = await cluster.ReadStoppedDocumentAsync("gru", id, cancellationToken);
        FaultSoakChecks.Check(stored?["value"]?.GetValue<string>() == expected, "the acknowledged document is absent before restart");
        await cluster.RestartAsync("gru", cancellationToken);
        var status = HttpStatusCode.Unused;
        var body = string.Empty;
        try
        {
            await FaultSoakChecks.UntilAsync(async () =>
            {
                using var response = await cluster["gru"].GetAsync(cluster.Document(id), cancellationToken);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(cancellationToken);
                return status == HttpStatusCode.OK && JsonNode.Parse(body)?["document"]?["value"]?.GetValue<string>() == expected;
            }, "the locally acknowledged document is served after SIGKILL restart", cancellationToken, TimeSpan.FromSeconds(180));
        }
        catch (TimeoutException error)
        {
            await cluster.KillAsync("gru");
            var recovered = await cluster.ReadStoppedDocumentAsync("gru", id, cancellationToken);
            throw new InvalidOperationException($"restart HTTP {(int)status}: {body}; stopped document before={stored}; after={recovered}\n{cluster.Diagnostics}", error);
        }
    }
}
