using System.Diagnostics;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Features.Management;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;
using TerraBase.Tenancy;

internal static class CertificationProof
{
    internal static async Task RunAsync(IServiceProvider services, CancellationToken token)
    {
        var engine = services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenSystemDatabaseAsync(token);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var organization = EntityIds.New().ToString();
        var project = EntityIds.New().ToString();
        var revision = new DatabaseRevision(EntityIds.New(), 1, 1).ToString();
        for (var batch = 0; batch < 100; batch++)
        {
            await using var transaction = await store.BeginDurableTransactionAsync(token);
            for (var index = 0; index < 100; index++)
            {
                var id = EntityIds.New().ToString();
                var storage = new TenantContext(organization, PlanTier.Hobby, project, id);
                var database = new ManagedDatabase(id, project, id, null, now, "gru");
                var retained = new DeletedDatabaseRecord(storage, database, now, now.AddDays(7), revision);
                var json = EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(retained, ManagementJsonContext.Default.DeletedDatabaseRecord), id, nested: true);
                await transaction.PutAsync(DeletedDatabases.Kind, id, json, token);
            }
            await transaction.CommitAsync(token);
        }
        var reconciler = services.GetRequiredService<RetainedReconciler>();
        for (var sample = 0; sample < 3; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            var proof = await reconciler.CertifyAsync(token);
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (proof.Count != 10_000 || proof.Missing != 10_000 || elapsed >= TimeSpan.FromSeconds(30))
            {
                throw new InvalidOperationException($"certification proof failed: count={proof.Count}, missing={proof.Missing}, elapsed={elapsed}");
            }
            Console.WriteLine($"CERTIFICATION_10000 sample={sample + 1} count={proof.Count} missing={proof.Missing} elapsed_ms={elapsed.TotalMilliseconds:F2}");
        }
        Console.WriteLine("CERTIFICATION_10000 synthetic=10000 distinct retained catalog records with valid identities and positions, deliberately absent files; actual production CertifyAsync, no handoff or transfer");
    }
}
