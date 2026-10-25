using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed partial class DatabaseRecoveryTests
{
    [Test]
    [Arguments("orphan")]
    [Arguments("registered")]
    [Arguments("intent")]
    [Arguments("unsupported")]
    [Arguments("restore")]
    public async Task Only_files_still_unowned_after_twenty_four_hours_are_closed_and_deleted(string ownership, CancellationToken token)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(services: ManualAudit, time: time);
        var registered = await api.CreateManagedDatabaseAsync();
        var orphan = new TenantContext(registered.Organization, PlanTier.Hobby, registered.Project, EntityIds.New().ToString());
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        await engine.CreateDatabaseAsync(orphan, token);
        await AuditAsync(api, token);
        time.Advance(TimeSpan.FromHours(23));
        await AuditAsync(api, token);
        var path = Path.Combine(api.Server.DataDirectory, orphan.OrganizationId, orphan.ProjectId!, orphan.DatabaseId + ".db");
        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(await ObservationAsync(api, orphan)).IsNotNull();
        // A peer may have removed the replicated cleanup intent while PITR is still active.
        await using var activeRestore = ownership == "restore" ? await api.Server.Services.GetRequiredService<IOrphanDatabaseFiles>().ProtectRestoreAsync(orphan, token) : null;
        var system = await engine.OpenSystemDatabaseAsync(token);
        await using (var transaction = await system.BeginTransactionAsync(write: true, token))
        {
            if (ownership == "registered")
            {
                await SystemCatalog.PutAsync(transaction, $"projects/{orphan.ProjectId}/databases", orphan.DatabaseId!,
                    new ManagedDatabase(orphan.DatabaseId!, orphan.ProjectId!, "adopted", null, time.GetUtcNow()), TerraBaseJsonContext.Default.ManagedDatabase, token);
            }
            if (ownership == "intent")
            {
                await SystemCatalog.PutAsync(transaction, "pending_database_cleanup", orphan.DatabaseId!, orphan, TerraBaseJsonContext.Default.TenantContext, token);
            }
            if (ownership == "unsupported")
            {
                await transaction.PutAsync("catalog", "version", "\"99\""u8.ToArray(), token);
            }
            await transaction.CommitAsync(token);
        }
        var opened = (await engine.OpenDatabaseAsync(orphan, token))!;
        time.Advance(TimeSpan.FromHours(2));
        await AuditAsync(api, token);
        await Assert.That(File.Exists(path)).IsEqualTo(ownership != "orphan");
        await Assert.That(File.Exists(path + ".key")).IsEqualTo(ownership != "orphan");
        await Assert.That(opened.IsClosed).IsEqualTo(ownership == "orphan");
        if (ownership == "unsupported")
        {
            await Assert.That(await ObservationAsync(api, orphan)).IsNotNull();
        }
        else
        {
            await Assert.That(await ObservationAsync(api, orphan)).IsNull();
        }
        if (ownership == "orphan")
        {
            await Assert.That(File.Exists(path + "-wal")).IsFalse();
            await AssertUnknownCountAsync(api, 0);
        }
    }

    [Test]
    public async Task Unrelated_lifecycle_contention_does_not_reset_an_orphans_first_seen(CancellationToken token)
    {
        await using var api = await ManagementApi.StartAsync(services: ManualAudit);
        var registered = await api.CreateManagedDatabaseAsync();
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var orphan = new TenantContext(registered.Organization, PlanTier.Hobby, registered.Project, EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(orphan, token);
        await AuditAsync(api, token);
        var before = (await ObservationAsync(api, orphan))!.FirstSeen;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creation = engine.CreateDatabaseWithIntentAsync(orphan with { DatabaseId = EntityIds.New().ToString() }, null, async _ =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return false;
        }, token).AsTask();
        try
        {
            await entered.Task.WaitAsync(token);
            await AuditAsync(api, token);
            await Assert.That((await ObservationAsync(api, orphan))!.FirstSeen).IsEqualTo(before);
        }
        finally
        {
            release.TrySetResult();
            await creation;
        }
    }
}
