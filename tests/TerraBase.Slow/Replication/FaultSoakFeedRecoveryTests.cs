using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.Slow.Replication;

[Category("replication-fault-soak")]
public sealed class FaultSoakFeedRecoveryTests
{
    [Test]
    [Timeout(240_000)]
    public async Task Feed_resumes_after_its_region_restarts_with_two_healthy_peers(CancellationToken cancellationToken)
    {
        await using var cluster = await FaultSoakCluster.StartAsync(0, cancellationToken);
        var feed = await FaultSoakFeed.StartAsync(() => cluster["gru"],
            $"{cluster.DatabasePath}/changes?collections={cluster.Collection}", cancellationToken);
        var failures = new List<Exception>();
        try
        {
            var before = EntityIds.New().ToString();
            var original = new JsonObject { ["marker"] = "before-restart" };
            using (var answer = await cluster["gru"].PutAsJsonAsync(cluster.Document(before), original, cancellationToken))
            {
                await FaultSoakChecks.SuccessAsync(answer, cancellationToken);
            }

            await FaultSoakChecks.UntilAsync(async () =>
            {
                var copies = await Task.WhenAll(cluster.Regions.Select(region =>
                    FaultSoakChecks.ReadAsync(cluster[region], cluster.Document(before), cancellationToken)));
                return copies.All(copy => FaultSoakChecks.Canonical(copy) == FaultSoakChecks.Canonical(original))
                    && feed.Has(before, "put", original);
            }, "the acknowledged write reaches all three replicas and the original feed", cancellationToken);

            // Qualification requires uninterrupted resume; the current refill contract permits explicit cursor expiry.
            // Only the feed's region stops; healthy peers retain the acknowledged write throughout recovery.
            await cluster.KillAsync("gru");
            await cluster.RestartAsync("gru", cancellationToken);
            await FaultSoakChecks.UntilAsync(async () =>
                FaultSoakChecks.Canonical(await FaultSoakChecks.ReadAsync(cluster["gru"], cluster.Document(before), cancellationToken))
                    == FaultSoakChecks.Canonical(original),
                "gru serves its acknowledged document after SIGKILL restart", cancellationToken, TimeSpan.FromSeconds(180));

            feed.ThrowIfFailed();
            var after = EntityIds.New().ToString();
            var fresh = new JsonObject { ["marker"] = "after-restart" };
            using (var answer = await cluster["gru"].PutAsJsonAsync(cluster.Document(after), fresh, cancellationToken))
            {
                await FaultSoakChecks.SuccessAsync(answer, cancellationToken);
            }

            await FaultSoakChecks.UntilAsync(() =>
            {
                feed.ThrowIfFailed();
                return Task.FromResult(feed.ResumedConnections > 0 && feed.Has(after, "put", fresh));
            }, "the same feed resumes with Last-Event-ID and delivers the post-restart acknowledged write", cancellationToken);
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        finally
        {
            try
            {
                await feed.DisposeAsync();
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(cluster.Diagnostics, failures);
        }
    }
}
