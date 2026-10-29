using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

/// <summary>
/// Batch 16 (2026-10-26): one usage flush saved every database's counts in one SYSTEM transaction and held the writer for up
/// to 4.3 s on gru. A flush now saves at most <see cref="UsageMeter.FlushDatabases"/> databases per transaction.
/// </summary>
public sealed class UsageFlushChunkTests
{
    [Test]
    public async Task A_flush_saves_at_most_a_chunk_of_databases_per_transaction_and_keeps_every_count()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartIsolatedAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        string[] names = [.. Enumerable.Range(0, UsageMeter.FlushDatabases + 4).Select(i => $"usage{i}")];
        List<string> databases = [];
        foreach (var name in names)
        {
            (await api.SendAsync(HttpMethod.Put, api.DatabasePath(name))).Status.ShouldBe(HttpStatusCode.Created);
            databases.Add((await api.TenantOfAsync(name, token)).DatabaseId!);
            (await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath(name)}/collections", new { name = DocumentApi.Collection })).Status
                .ShouldBe(HttpStatusCode.Created);
            (await api.SendAsync(HttpMethod.Put,
                $"{api.DatabasePath(name)}/collections/{DocumentApi.Collection}/documents/by-key?id={DocumentApi.NewId()}", new { n = 1 })).Status
                .ShouldBe(HttpStatusCode.Created);
        }

        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(token);

        // Each transaction saves its own flush id beside the counts of every database it saved.
        var machine = api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId;
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        Dictionary<string, int> perFlush = [];
        await using (var read = await system.BeginTransactionAsync(write: false, token))
        {
            foreach (var database in databases)
            {
                var flush = await read.GetAsync(SystemCatalog.DatabaseState(database) + "usage", "flush~" + machine, token);
                await Assert.That(flush).IsNotNull().Because("every database's counts were saved");
                var id = Encoding.UTF8.GetString(flush!.Json.Span);
                perFlush[id] = perFlush.GetValueOrDefault(id) + 1;
            }
        }

        await Assert.That(perFlush.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(perFlush.Values.Max()).IsLessThanOrEqualTo(UsageMeter.FlushDatabases);
    }
}
