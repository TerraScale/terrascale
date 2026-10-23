using System.Diagnostics.Metrics;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class StoreCreationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Creation_pins_its_own_incarnation_before_a_delete_queued_at_publication(bool copy)
    {
        var fake = new Faker();
        var target = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby,
            EntityIds.New().ToString(), EntityIds.New().ToString());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        TursoStorageEngine? engine = null;
        TursoDatabaseStore? initialized = null;
        Task<bool>? deletion = null;
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<TerraBaseOptions>();
            engine = new TursoStorageEngine(options.DataDirectory, options.StorageThreads, options.StorageQueue, options.MemoryBudget,
                provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                provider.GetRequiredService<ILogger<TursoStorageEngine>>(), provider.GetRequiredKeyedService<IMasterKey>(EngineExtensions.DatabaseMasterKey), options.ShutdownGrace, readinessCacheTtl: options.ReadinessCacheTtl,
                checkpoint: (step, database) =>
                {
                    if (step == "create.published" && database == target && deletion is null)
                    {
                        // Queue the deletion while publication still owns the lifecycle lock.
                        deletion = engine!.DeleteDatabaseAsync(target, deadline.Token).AsTask();
                    }
                }, afterBootstrap: store =>
                {
                    if (store.Tenant == target)
                    {
                        initialized = store;
                    }
                });
            return engine;
        }));
        try
        {
            var source = target with { DatabaseId = EntityIds.New().ToString() };
            if (copy)
            {
                await engine!.CreateDatabaseAsync(source, deadline.Token);
            }

            var created = copy
                ? await engine!.CopyDatabaseAsync(source, target, deadline.Token)
                : await engine!.CreateDatabaseAsync(target, deadline.Token);
            await Assert.That(created).IsTrue();
            await Assert.That(deletion is not null).IsTrue();
            await Assert.That(await deletion!).IsTrue();
            await Assert.That(initialized).IsNotNull();
            var original = initialized!;
            await Assert.That(original.IsClosed).IsTrue();
            await Assert.That(await engine.OpenDatabaseAsync(target, deadline.Token)).IsNull();
            await Assert.That(await engine.CreateDatabaseAsync(target, deadline.Token)).IsTrue();
            var replacement = (await engine.OpenDatabaseAsync(target, deadline.Token))!;
            await Assert.That(replacement.Incarnation).IsNotEqualTo(original.Incarnation);
            using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), deadline.Token);
            health.EnsureSuccessStatusCode();
        }
        finally
        {
            if (deletion is not null)
            {
                await deletion;
            }
        }
    }
}
