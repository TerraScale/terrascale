using System.Net;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.Slow.Replication;

[Category("replication-fault-soak")]
public sealed class FaultSoakRecoveryTests
{
    [Test]
    [Timeout(180_000)]
    public async Task Local_ack_survives_restart(CancellationToken cancellationToken)
    {
        await using var cluster = await FaultSoakCluster.StartLocalAsync(0, cancellationToken);
        var id = EntityIds.New().ToString();
        const string expected = "restart-liveness";
        using (var answer = await cluster["gru"].PutAsJsonAsync(cluster.Document(id), new { value = expected }, cancellationToken))
        {
            await FaultSoakChecks.SuccessAsync(answer, cancellationToken);
        }

        // No WAL truncation: this regression isolates restart serving from the simulated power-loss model.
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
            }, "the locally acknowledged document is served after SIGKILL restart", cancellationToken);
        }
        catch (TimeoutException error)
        {
            await cluster.KillAsync("gru");
            var recovered = await cluster.ReadStoppedDocumentAsync("gru", id, cancellationToken);
            throw new InvalidOperationException($"restart HTTP {(int)status}: {body}; stopped document before={stored}; after={recovered}\n{cluster.Diagnostics}", error);
        }
    }
}
