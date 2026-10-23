using System.Diagnostics.Metrics;
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

// TS-624: a store's initialization took its own storage admission while its creator still held the creation admission,
// so storage-queue=1 refused it about 1 run in 30 under load. It now starts only once the creator released the lock.
public sealed class StoreInitializationTests
{
    [Test]
    public async Task A_store_initializes_only_after_its_creator_released_the_creation_lock()
    {
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        TursoStorageEngine? engine = null;
        var bootstrappedUnderLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<TerraBaseOptions>();
            engine = new TursoStorageEngine(options.DataDirectory, options.StorageThreads, options.StorageQueue, options.MemoryBudget,
                provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                provider.GetRequiredService<ILogger<TursoStorageEngine>>(), provider.GetRequiredKeyedService<IMasterKey>(EngineExtensions.DatabaseMasterKey), options.ShutdownGrace, readinessCacheTtl: options.ReadinessCacheTtl,
                checkpoint: (step, tenant) =>
                {
                    // Still inside the creation lock, right after the store's initialization was registered: one that
                    // starts now would bootstrap while the lock is held. Wait until it did, or until it waits for the release.
                    if (step == "open.registered" && tenant == database)
                    {
                        SpinWait.SpinUntil(() => bootstrappedUnderLock.Task.IsCompleted || engine!.InitializationsAwaitingRelease > 0, TimeSpan.FromSeconds(30));
                    }
                },
                afterBootstrap: store =>
                {
                    if (store.Tenant == database)
                    {
                        bootstrappedUnderLock.TrySetResult(engine!.CreationHeld);
                    }
                });
            return engine;
        }));

        await Assert.That(await engine!.CreateDatabaseAsync(database, CancellationToken.None)).IsTrue();
        await Assert.That(await bootstrappedUnderLock.Task).IsFalse();
    }
}
