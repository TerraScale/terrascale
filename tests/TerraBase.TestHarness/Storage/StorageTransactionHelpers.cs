using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Hosting;

namespace TerraBase.Fast.Storage;

public sealed partial class StorageTransactionTests
{
    internal static void ConfigurePool(IServiceCollection services)
    {
        // Explicit limits keep pool assertions and fixture size independent of the machine's CPU count.
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<TerraBaseOptions>();
            return new TursoStorageEngine(
                options.DataDirectory, options.StorageThreads, options.StorageQueue, options.MemoryBudget,
                provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                provider.GetRequiredService<ILogger<TursoStorageEngine>>(), provider.GetRequiredKeyedService<IMasterKey>(EngineExtensions.DatabaseMasterKey), options.ShutdownGrace, maxIdle: 8, maxIdlePerDatabase: 2, backupTime: provider.GetRequiredService<TimeProvider>());
        });
    }

    internal static async Task DisposeBeginAsync(Task<IStorageTransaction>? begin)
    {
        if (begin is null)
        {
            return;
        }

        IStorageTransaction transaction;
        try
        {
            transaction = await begin;
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            // Refused BEGINs have no transaction to dispose; their assertions run before cleanup.
            return;
        }

        await transaction.DisposeAsync();
    }

}
