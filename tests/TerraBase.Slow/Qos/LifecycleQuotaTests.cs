using TerraBase.Testing;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Slow.Documents;
using TerraBase.Features.Management;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Slow.Qos;

public sealed class LifecycleQuotaTests
{
    [Test]
    public async Task Bounded_accounting_failure_returns_503_and_enterprise_copies_skip_the_failed_scan()
    {
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IStorageQuota>(provider =>
            new FailedAccountingQuota(new StorageQuota(provider.GetRequiredService<IStorageEngine>(), provider.GetRequiredService<ITenantDirectory>()))));
        (await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        var database = await api.TenantOfAsync();
        using var owner = api.Management.As(api.Tenant.User);
        var refused = await DocumentApi.SendAsync(owner, HttpMethod.Post, Managed(database) + "/branches", JsonContent.Create(new { name = "copy" }));
        await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.ServiceUnavailable, "storage_accounting_busy"));
        await Assert.That(await DatabaseCountAsync(api)).IsEqualTo(1);
        await api.Server.Services.GetRequiredService<IManagementService>()
            .SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, PlanTier.Enterprise, CancellationToken.None);
        var branch = await DocumentApi.SendAsync(owner, HttpMethod.Post, Managed(database) + "/branches", JsonContent.Create(new { name = "copy" }));
        await Assert.That(branch.Status).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task A_branch_is_refused_before_registration_and_shrinking_the_source_allows_a_retry()
    {
        await using var api = await StartAsync();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { data = new string('x', 21) })).Status.ShouldBe(HttpStatusCode.Created);
        var database = await api.TenantOfAsync();
        using var owner = api.Management.As(api.Tenant.User);
        var path = Managed(database) + "/branches";
        await IsQuotaAsync(await DocumentApi.SendAsync(owner, HttpMethod.Post, path, JsonContent.Create(new { name = "copy" })));
        await Assert.That(await DatabaseCountAsync(api)).IsEqualTo(1);
        (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.OK);
        var accepted = await DocumentApi.SendAsync(owner, HttpMethod.Post, path, JsonContent.Create(new { name = "copy" }));
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await DatabaseCountAsync(api)).IsEqualTo(2);
    }

    [Test]
    public async Task Restore_charges_the_exact_backup_snapshot_and_does_not_charge_the_backup_itself()
    {
        await using var api = await StartAsync();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { data = new string('x', 21) })).Status.ShouldBe(HttpStatusCode.Created);
        var database = await api.TenantOfAsync();
        using var owner = api.Management.As(api.Tenant.User);
        var backup = await DocumentApi.SendAsync(owner, HttpMethod.Post, Managed(database) + "/backups");
        backup.Status.ShouldBe(HttpStatusCode.Created);
        var backupId = backup.Json.GetProperty("id").GetString();
        (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.OK);
        await IsQuotaAsync(await DocumentApi.SendAsync(owner, HttpMethod.Post, Managed(database) + $"/backups/{backupId}/restore", JsonContent.Create(new { name = "restored" })));
        await Assert.That(await DatabaseCountAsync(api)).IsEqualTo(2); // The source and its uncharged backup remain.
        var branch = await DocumentApi.SendAsync(owner, HttpMethod.Post, Managed(database) + "/branches", JsonContent.Create(new { name = "smaller-copy" }));
        await Assert.That(branch.Status).IsEqualTo(HttpStatusCode.Created);
    }

    private static Task<DocumentApi> StartAsync() => DocumentApi.StartAsync(services: services => services.AddSingleton<IStorageQuota>(provider =>
        new StorageQuota(provider.GetRequiredService<IStorageEngine>(), provider.GetRequiredService<ITenantDirectory>(), _ => 32)));

    private static string Managed(TenantContext database) =>
        $"/v1/management/organizations/{database.OrganizationId}/projects/{database.ProjectId}/databases/{database.DatabaseId}";

    private static async Task<int> DatabaseCountAsync(DocumentApi api)
    {
        var count = 0;
        await foreach (var _ in api.Server.Services.GetRequiredService<IStorageEngine>().ListDatabasesAsync(CancellationToken.None))
        {
            count++;
        }

        return count;
    }

    private static async Task IsQuotaAsync(ApiResponse response) =>
        await Assert.That((response.Status, response.Code)).IsEqualTo((HttpStatusCode.TooManyRequests, "storage_quota"));

    // Integration fault injection: preserve real admission and catalog publication, fail only staged accounting.
    private sealed class FailedAccountingQuota(StorageQuota inner) : IStorageQuota, IDisposable
    {
        public ValueTask<StorageQuotaEntry> EnterAsync(TenantContext database, IDatabaseStore store, CancellationToken cancellationToken) =>
            inner.EnterAsync(database, store, cancellationToken);

        public ValueTask<StorageQuotaGroupEntry> EnterOrganizationsAsync(IReadOnlyList<string> organizationIds, CancellationToken cancellationToken) =>
            inner.EnterOrganizationsAsync(organizationIds, cancellationToken);

        public ValueTask<long> ReadBytesAsync(TenantContext database, CancellationToken cancellationToken) =>
            throw new QueryCapacityException("injected accounting capacity");

        public void Dispose() => inner.Dispose();
    }
}
