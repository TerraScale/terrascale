using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class PlatformRestoreFenceTests
{
    private const string Code = "recovery_erasure_fenced";
    private const string Message = "platform database historical recovery is fenced by erasure requirements";

    [Test]
    [Arguments(PlatformDatabases.AuthProjectId, PlatformDatabases.Auth)]
    [Arguments(PlatformDatabases.DashboardProjectId, PlatformDatabases.Dashboard)]
    public async Task Platform_sources_are_refused_before_restore_work_with_or_without_receipts(string project, string name, CancellationToken cancellationToken)
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            s3.Configure(settings);
            settings["auth-issuer"] = TerraBaseOptions.PlatformAuthIssuer;
            settings["auth-audience"] = TerraBaseOptions.PlatformAuthAudience;
        }, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
            services.AddHttpClient("ts-auth").AddHttpMessageHandler(() => new GlobalIssuerRoute());
        }, time, cancellationToken);
        await Assert.That(await api.Server.Services.GetRequiredService<IPlatformCatalog>().EnsurePlatformAsync(cancellationToken) is Done).IsTrue();
        using var client = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Issuer = TerraBaseOptions.PlatformAuthIssuer;
            token.Audience = TerraBaseOptions.PlatformAuthAudience;
            token.Claims["org_id"] = PlatformDatabases.OrganizationId;
            token.Claims["project_id"] = project;
            token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
        }));
        var database = new ManagedDatabaseRef(PlatformDatabases.OrganizationId, project, PlatformDatabases.Databases[name].Id, name);
        await ManagementApi.CreateCollectionAsync(client, database, "people", cancellationToken);
        var document = ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "saved"));
        var text = new Faker().Lorem.Sentence();
        using var written = await client.PutAsJsonAsync(document, new { text }, cancellationToken);
        written.EnsureSuccessStatusCode();
        using var created = await client.PostAsync(ManagementApi.Uri(database.Data + "/backups"), content: null, cancellationToken);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var backup = (await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetString()!;
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
        using var window = await client.GetAsync(ManagementApi.Uri(database.Data + "/point-in-time"), cancellationToken);
        window.EnsureSuccessStatusCode();
        var at = (await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("latest").GetDateTimeOffset();
        var catalogRoute = ManagementApi.Uri($"/v1/projects/{project}/databases");
        var catalog = await client.GetStringAsync(catalogRoute, cancellationToken);
        var files = Directory.GetFiles(api.Server.DataDirectory, "*.db", SearchOption.AllDirectories);
        var sourcePrefix = $"v1/{database.Organization}/{project}/{database.Id}/";
        var reads = 0;
        s3.BeforeGet = ObserveRead;
        s3.BeforeList = ObserveRead;
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(cancellationToken);
        var erasure = EntityIds.New().ToString();
        foreach (var hasReceipt in new[] { false, true })
        {
            if (hasReceipt)
            {
                await using var write = await system.BeginTransactionAsync(write: true, cancellationToken);
                var receipt = new HistoryErasureReceipt(erasure, "holder", 1, HistoryErasure.Fenced, 1, EntityIds.New().ToString(), time.GetUtcNow());
                await SystemCatalog.PutAsync(write, HistoryErasure.Receipts(erasure), "holder", receipt, TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken);
                await write.CommitAsync(cancellationToken);
            }

            await using (var read = await system.BeginTransactionAsync(write: false, cancellationToken))
            {
                var receipts = await read.ListAsync(HistoryErasure.Receipts(erasure), null, 1, 4096, cancellationToken);
                await Assert.That(receipts.Documents.Count).IsEqualTo(hasReceipt ? 1 : 0);
            }

            foreach (var id in new[] { backup, EntityIds.New().ToString() })
            {
                await ErrorAssert.ErrorAsync(client.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/backups/{id}/restore"), new { name = "refused" }, cancellationToken),
                    HttpStatusCode.Conflict, Code, Message);
            }

            foreach (var destination in new[] { "platform", "customer" })
            {
                await ErrorAssert.ErrorAsync(client.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/point-in-time/restore"), new { pointInTime = at, name = "refused", destination }, cancellationToken),
                    HttpStatusCode.Conflict, Code, Message);
            }
            var source = new TenantContext(database.Organization, PlanTier.Enterprise, project, database.Id);
            var result = await api.Server.Services.GetRequiredService<IPointInTimeRestore>().RestoreAsync(source, at, source with { DatabaseId = EntityIds.New().ToString() }, cancellationToken);
            await Assert.That(result is Conflict { Code: Code, Message: Message }).IsTrue();
        }

        await Assert.That(reads).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync(catalogRoute, cancellationToken)).IsEqualTo(catalog);
        await Assert.That(Directory.GetFiles(api.Server.DataDirectory, "*.db", SearchOption.AllDirectories)).IsEquivalentTo(files);
        using var kept = await client.GetAsync(document, cancellationToken);
        kept.EnsureSuccessStatusCode();
        await Assert.That((await kept.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);

        Task ObserveRead(string key, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (key.StartsWith(sourcePrefix, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref reads);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class GlobalIssuerRoute : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(request.RequestUri!.AbsoluteUri.Replace(TerraBaseOptions.PlatformAuthIssuer, TestIssuer.Issuer, StringComparison.Ordinal));
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri.AbsoluteUri == TestIssuer.Discovery && response.IsSuccessStatusCode)
            {
                var discovery = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Content.Dispose();
                response.Content = new StringContent(discovery.Replace(TestIssuer.Issuer, TerraBaseOptions.PlatformAuthIssuer, StringComparison.Ordinal), Encoding.UTF8, "application/json");
            }

            return response;
        }
    }
}
