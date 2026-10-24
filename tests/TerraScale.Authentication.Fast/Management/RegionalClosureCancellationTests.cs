using System.Text.Json;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>Real regional writers, delayed peer streams, and the same all-holder handoff account cancellation uses.</summary>
public sealed class RegionalClosureCancellationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancellation_fences_remote_deferrals_and_preserves_irreversible_work(bool committed)
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        var first = new AuthStore(regions.Database("gru"), TimeProvider.System);
        var second = new AuthStore(regions.Database("sjc"), TimeProvider.System);
        var user = AuthStore.NewId();
        var key = ClosureJob.Key("account", user);
        var (job, create) = ClosureJob.New("terrascale", "account", user, null, user, TimeProvider.System);
        await first.TransactAsync([create]);
        await Assert.That(await ReadinessWait.UntilAsync(async () =>
            await second.GetAsync(AuthStore.ClosureJobCollection, "terrascale", key) is not null, TimeSpan.FromSeconds(15))).IsTrue();
        var remote = (await second.GetAsync(AuthStore.ClosureJobCollection, "terrascale", key))!;
        regions.HoldReplication();
        Task<bool>? defer = null;
        Task<string?>? commit = null;
        try
        {
            // The first holder has installed its fence, but cannot claim cancellation while the remote writer is live.
            var pending = await first.CancelAccountClosureAsync(job);
            await Assert.That(pending.Status).IsEqualTo("pending");
            await Assert.That(pending.PendingHolders).IsEqualTo(1);
            await Assert.That(await first.GetAsync(AuthStore.ClosureJobCollection, "terrascale", key)).IsNotNull();

            if (committed)
            {
                commit = second.CommitAccountClosureAsync(job with { CommittedAt = DateTimeOffset.UtcNow }, remote.Version);
                await Assert.That(await ReadinessWait.UntilAsync(async () => await second.ClosureCommittedAtAsync(job) is not null,
                    TimeSpan.FromSeconds(5))).IsTrue();
            }
            else
            {
                defer = second.CompareAndPutAsync(AuthStore.ClosureJobCollection, "terrascale", key,
                    JsonSerializer.SerializeToElement(job with { Attempts = 1 }, ClosureJson.Default.ClosureJob), remote.Version);
                await Assert.That(await ReadinessWait.UntilAsync(async () =>
                    (await second.GetAsync(AuthStore.ClosureJobCollection, "terrascale", key))?.Document.GetProperty("attempts").GetInt32() == 1,
                    TimeSpan.FromSeconds(5))).IsTrue();
            }
        }
        finally
        {
            regions.ReleaseReplication();
        }

        if (commit is not null)
        {
            await Assert.That(await commit).IsNotNull();
        }
        else
        {
            await Assert.That(await defer!).IsTrue();
        }

        var wanted = committed ? "committed" : "cancelled";
        await Assert.That(await ReadinessWait.UntilAsync(async () =>
            (await first.CancelAccountClosureAsync(job)).Status == wanted, TimeSpan.FromSeconds(30))).IsTrue();

        foreach (var store in new[] { first, second })
        {
            await Assert.That((await store.CancelAccountClosureAsync(job)).Status).IsEqualTo(wanted);
            if (committed)
            {
                // Cancellation must not remove the driver of already irreversible work.
                await Assert.That(await store.GetAsync(AuthStore.ClosureJobCollection, "terrascale", key)).IsNotNull();
            }
            else
            {
                await Assert.That(await store.GetAsync(AuthStore.ClosureJobCollection, "terrascale", key)).IsNull();
                await Assert.That(await store.CompareAndPutAsync(AuthStore.ClosureJobCollection, "terrascale", key,
                    JsonSerializer.SerializeToElement(job with { Attempts = 2 }, ClosureJson.Default.ClosureJob), remote.Version)).IsFalse();
                await Assert.That(await store.CommitAccountClosureAsync(job with { CommittedAt = DateTimeOffset.UtcNow }, remote.Version)).IsNull();
            }
        }

        foreach (var region in RegionalTerraBaseFixture.Regions)
        {
            var marker = (await regions.Database(region).GetDocumentAsync(AuthStore.ClosureCancellationCollection, job.Generation!))!;
            await Assert.That(!marker.Document.TryGetProperty("targetId", out var target) || target.ValueKind == JsonValueKind.Null).IsTrue();
        }

        if (!committed)
        {
            // The fence is per creation. Neither replaying the cancellation nor its late receipt touches a new intent.
            var (replacement, replace) = ClosureJob.New("terrascale", "account", user, null, user, TimeProvider.System);
            await Assert.That(await first.TransactAsync([replace])).IsNull();
            await Assert.That((await first.CancelAccountClosureAsync(job)).Status).IsEqualTo("cancelled");
            await Assert.That(await ReadinessWait.UntilAsync(async () =>
                (await second.GetAsync(AuthStore.ClosureJobCollection, "terrascale", key))?.Document.GetProperty("generation").GetString() == replacement.Generation,
                TimeSpan.FromSeconds(15))).IsTrue();
        }
    }
}
