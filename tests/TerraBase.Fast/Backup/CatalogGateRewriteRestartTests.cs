using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

/// <summary>
/// A healthy rewrite of the catalog (a history erasure's) replaces its file under a new incarnation without moving its head.
/// A graceful restart right after it, as a rolling deploy does, opens the gate again: the restore anchor follows the
/// catalog's identity, not only its head, so the rewritten file is not mistaken for a restored older copy.
/// </summary>
public sealed class CatalogGateRewriteRestartTests
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(10);

    [Test]
    public async Task A_region_restarted_right_after_its_catalog_was_rewritten_opens_its_gate()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var bucket = new FakeS3();
        var members = new FakeMemberStore();
        await using var gru = new RestartingMachine("gru", bucket, members);
        await using var sjc = new RestartingMachine("sjc", bucket, members);
        await using var fra = new RestartingMachine("fra", bucket, members);
        foreach (var machine in (RestartingMachine[])[gru, sjc, fra])
        {
            await machine.StartAsync();
        }

        foreach (var machine in (RestartingMachine[])[gru, sjc, fra])
        {
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(machine.Gate.IsOpen), timeout: TimeSpan.FromSeconds(30));
        }

        _ = await gru.Api.CreateOrganizationAsync("anchored before the rewrite");
        await Assert.That(await gru.Gate.TryAnchorAsync(token)).IsTrue();

        var rewrite = await gru.Api.Server.Services.GetRequiredService<IHealthyRewrite>().RewriteAsync(null, 0, token);
        await Assert.That(rewrite.TargetIncarnation).IsNotEqualTo(rewrite.SourceIncarnation);

        await gru.StopAsync();
        await gru.StartAsync();
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(gru.Gate.IsOpen), timeout: Within);
        await Assert.That(gru.Gate.Trusted).IsTrue();
        var catalog = await gru.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await Assert.That(catalog.Incarnation).IsEqualTo(rewrite.TargetIncarnation);
    }
}
