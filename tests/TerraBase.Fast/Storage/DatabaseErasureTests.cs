using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Hosting;
using TerraBase.Testing;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Storage;

public sealed class DatabaseErasureTests
{
    [Test]
    [Arguments("copy")]
    [Arguments("measure")]
    [Arguments("read")]
    public async Task Salvage_operations_hold_erasure_proof_and_cannot_restart_after_customer_deletion(string operation)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var armed = 0;
        var checkpoint = operation == "copy" ? "rebuild.salvage_copy" : "rebuild.salvage_reader";
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
            {
                if (step == checkpoint && Volatile.Read(ref armed) == 1)
                {
                    reached.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(30), token))
                    {
                        throw new TimeoutException("salvage reader was not released");
                    }
                }
            }));
        var managed = await api.CreateManagedDatabaseAsync();
        var database = new TenantContext(managed.Organization, PlanTier.Hobby, managed.Project, managed.Id);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var incarnation = await engine.IncarnationAsync(database, token) ?? throw new InvalidOperationException("database is absent");
        var side = engine.SidePath(database, incarnation);
        await Assert.That(await engine.SwapAsync(database, incarnation, side, token)).IsNotNull();
        var working = operation == "copy" ? side + ".salvage" : await engine.CopyForSalvageAsync(side, token);
        var rows = operation == "read"
            ? await engine.MeasureSideAsync(working, string.Empty, string.Empty, 100, 1024 * 1024, token)
            : [];
        Volatile.Write(ref armed, 1);
        async Task RunAsync()
        {
            if (operation == "copy")
            {
                _ = await engine.CopyForSalvageAsync(side, token);
            }
            else if (operation == "measure")
            {
                _ = await engine.MeasureSideAsync(working, string.Empty, string.Empty, 100, 1024 * 1024, token);
            }
            else
            {
                _ = await engine.ReadSideAsync(working, rows, token);
            }
        }
        var running = RunAsync();
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            using var deleted = await api.Client.DeleteAsync(new Uri(managed.Path, UriKind.Relative), token);
            await Assert.That(deleted.StatusCode).IsEqualTo(System.Net.HttpStatusCode.NoContent);
            await Assert.That(await engine.VerifyDatabaseErasureAsync(database, token)).IsFalse();
        }
        finally
        {
            release.Set();
            try
            {
                await running;
            }
            catch (IOException) when (operation == "copy")
            {
                // The admitted copy resumes after its source was unlinked.
            }
        }

        await Assert.That(await engine.VerifyDatabaseErasureAsync(database, token)).IsTrue();
        await Assert.That(RunAsync).Throws<IOException>();
    }

    [Test]
    public async Task An_unlinked_database_is_not_verified_erased_while_a_reader_is_open()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        var database = await tenant.ContextAsync(api.Server, "main", token);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenDatabaseAsync(database, token) ?? throw new InvalidOperationException("database is absent");
        var reader = await store.BeginTransactionAsync(write: false, token);
        try
        {
            await Assert.That(await engine.DeleteDatabaseAsync(database, token)).IsTrue();
            await Assert.That(await engine.VerifyDatabaseErasureAsync(database, token)).IsFalse();
        }
        finally
        {
            await reader.DisposeAsync();
        }

        _ = await engine.DeleteDatabaseAsync(database, token);
        await Assert.That(await engine.VerifyDatabaseErasureAsync(database, token)).IsTrue();
    }

    [Test]
    public async Task An_unlinked_database_is_not_verified_erased_while_its_backup_reader_is_open()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        var database = await tenant.ContextAsync(api.Server, "main", token);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var tap = await engine.OpenTapAsync(database, token) ?? throw new InvalidOperationException("tap is absent");
        try
        {
            await Assert.That(await engine.DeleteDatabaseAsync(database, token)).IsTrue();
            await Assert.That(await engine.VerifyDatabaseErasureAsync(database, token)).IsFalse();
        }
        finally
        {
            await tap.DisposeAsync();
        }

        _ = await engine.DeleteDatabaseAsync(database, token);
        await Assert.That(await engine.VerifyDatabaseErasureAsync(database, token)).IsTrue();
    }
}
