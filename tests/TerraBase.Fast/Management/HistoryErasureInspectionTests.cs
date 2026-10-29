using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Turso;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class HistoryErasureInspectionTests
{
    [Test]
    public async Task Inspection_requires_internal_credentials_redacts_tokens_and_never_changes_the_catalog()
    {
        var clock = new FakeTimeProvider();
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        var erasure = EntityIds.New().ToString();
        var route = "/admin/history-erasures/" + erasure;
        var catalog = server.Services.GetRequiredService<SystemCatalog>();
        var system = server.Services.GetRequiredService<TursoStorageEngine>().SystemStore!;
        var before = system.CommitSequence;
        using (var absent = await server.Client.GetAsync(route))
        {
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }

        await Assert.That(system.CommitSequence).IsEqualTo(before).Because("a missing GET must not initialize the catalog or erasure");
        _ = await catalog.CheckAsync(CancellationToken.None);
        using (var absent = await server.Client.GetAsync(route))
        {
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        var record = new HistoryErasureRecord(erasure, clock.GetUtcNow(), clock.GetUtcNow().AddDays(30), clock.GetUtcNow().AddDays(60),
            ["local"], Requester: "local", SettlementToken: "must-never-leak", FinalId: EntityIds.New().ToString());
        var rewrite = new HistoryRewrite(EntityIds.New().ToString(), EntityIds.New().ToString(), 42);
        var receipt = new HistoryErasureReceipt(erasure, "local", 1, HistoryErasure.ResidueCleared, 2, rewrite.Source, clock.GetUtcNow(),
            FinalId: record.FinalId, Auth: rewrite, System: rewrite, Residue: rewrite, ResidueAt: clock.GetUtcNow());
        await catalog.WriteUncheckedAsync(async transaction =>
        {
            await SystemCatalog.PutAsync(transaction, HistoryErasure.Collection, erasure, record, TerraBaseJsonContext.Default.HistoryErasureRecord, CancellationToken.None);
            await SystemCatalog.PutAsync(transaction, HistoryErasure.Receipts(erasure), "local", receipt, TerraBaseJsonContext.Default.HistoryErasureReceipt, CancellationToken.None);
            return true;
        }, CancellationToken.None);
        before = system.CommitSequence;
        using (var inspected = await server.Client.GetAsync(route))
        {
            await Assert.That(inspected.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(inspected.Headers.CacheControl!.NoStore).IsTrue();
            var raw = await inspected.Content.ReadAsStringAsync();
            await Assert.That(raw).DoesNotContain("must-never-leak");
            await Assert.That(raw).DoesNotContain("settlementToken");
            using var json = JsonDocument.Parse(raw);
            await Assert.That(json.RootElement.GetProperty("erasureId").GetString()).IsEqualTo(erasure);
            await Assert.That(json.RootElement.GetProperty("receipts")[0].GetProperty("system").GetProperty("target").GetString()).IsEqualTo(rewrite.Target);
            await Assert.That(json.RootElement.GetProperty("receipts")[0].GetProperty("residue").GetProperty("offset").GetInt64()).IsEqualTo(42);
        }

        await Assert.That(system.CommitSequence).IsEqualTo(before).Because("inspection cannot settle, issue a token or run a rewrite");
        using var publicClient = new HttpClient { BaseAddress = server.ClientAddress };
        using var publicResponse = await publicClient.GetAsync(route);
        await Assert.That(publicResponse.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var unauthorized = new HttpClient { BaseAddress = server.BaseAddress, DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using var refused = await unauthorized.GetAsync(route);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var invalid = await server.Client.GetAsync("/admin/history-erasures/invalid");
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
