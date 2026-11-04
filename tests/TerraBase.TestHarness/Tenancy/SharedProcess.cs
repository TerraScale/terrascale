using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Hosting;

namespace TerraBase.Testing;

internal static class SharedProcess
{
    internal const string Endless = "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x + 1 FROM n) SELECT sum(x) FROM n";

    internal static void BoundWriters(IServiceCollection services, int maxWriters) => services.AddSingleton(provider =>
    {
        var options = provider.GetRequiredService<TerraBaseOptions>();
        return new TursoStorageEngine(
            options.DataDirectory, options.StorageThreads, options.StorageQueue, options.MemoryBudget,
            provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
            provider.GetRequiredService<ILogger<TursoStorageEngine>>(), provider.GetRequiredKeyedService<IMasterKey>(EngineExtensions.DatabaseMasterKey),
            options.ShutdownGrace, maxWriters: maxWriters, backupTime: provider.GetRequiredService<TimeProvider>(),
            schemaMemory: provider.GetRequiredService<TerraBase.Features.Memory.IMemoryBudget>(),
            nativeTemporaryStorage: provider.GetRequiredService<INativeTemporaryStorage>(), diskBudget: provider.GetRequiredService<IDiskBudget>());
    });

    internal static async Task<(HttpStatusCode Status, string Body)> SqlAsync(HttpClient client, string database, string sql, params object?[] args)
    {
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri(database + "/sql/execute"), new { statements = new[] { new { sql, args } } });
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    internal static double Percentile(List<double> samples, double percentile)
    {
        var sorted = samples.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1)];
    }
}
