using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Management;
using TerraBase.Features.Management;
using TerraBase.Features.Qos;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Qos;

public sealed class LifecycleQuotaTests
{
    [Test]
    public async Task Dropping_a_collection_waits_for_a_commit_that_is_using_its_database_deletion_credit()
    {
        var hook = new UsageCommitHook(storageLimit: _ => 64);
        await using var api = await DocumentApi.StartAsync(services: hook.Install);
        (await api.PutAsync(DocumentApi.NewId(), new { data = new string('x', 13) })).Status.ShouldBe(HttpStatusCode.Created); // 24 bytes.
        var source = await api.TenantOfAsync();
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath("removed"))).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Post, api.DatabasePath("removed") + "/collections", new { name = "items" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath("removed") + "/collections/items/documents/by-key?id=" + DocumentApi.NewId(),
            new { data = new string('x', 13) })).Status.ShouldBe(HttpStatusCode.Created);
        var removed = await api.TenantOfAsync("removed");
        using var owner = api.Management.As(api.Tenant.User);
        using var credited = new UsageCommitHook.Gate(fail: false);
        var dropQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hook.AfterQuotaBytes = (database, cancellationToken) => database.DatabaseId == removed.DatabaseId
            ? credited.WaitAsync(cancellationToken) : ValueTask.CompletedTask;
        hook.BeforeDocumentQuota = (database, _) =>
        {
            if (database.DatabaseId == removed.DatabaseId)
            {
                dropQueued.TrySetResult();
            }

            return ValueTask.CompletedTask;
        };
        var committing = CommitAsync(owner, Branch(source, "one"), Branch(source, "two"),
            new { op = "delete_database", organization = removed.OrganizationId, project = removed.ProjectId, database = removed.DatabaseId });
        await credited.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var dropping = api.SendAsync(HttpMethod.Delete, api.DatabasePath("removed") + "/collections/items");
        await dropQueued.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(dropping.IsCompleted).IsFalse();
        credited.Dispose();
        await IsQuotaAsync(await committing); // 48 live bytes plus 24 net growth would exceed 64.
        await Assert.That((await dropping).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await DatabaseCountAsync(api)).IsEqualTo(2);
        var original = await DocumentApi.SendAsync(owner, HttpMethod.Get, Managed(removed));
        await Assert.That(original.Status).IsEqualTo(HttpStatusCode.OK);
    }

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
        var failedCommit = await CommitAsync(owner, Branch(database, "copy"));
        await Assert.That((failedCommit.Status, failedCommit.Code)).IsEqualTo((HttpStatusCode.ServiceUnavailable, "storage_accounting_busy"));
        await Assert.That(await DatabaseCountAsync(api)).IsEqualTo(1);
        await api.Server.Services.GetRequiredService<IManagementService>()
            .SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, PlanTier.Enterprise, CancellationToken.None);
        var branch = await DocumentApi.SendAsync(owner, HttpMethod.Post, Managed(database) + "/branches", JsonContent.Create(new { name = "copy" }));
        await Assert.That(branch.Status).IsEqualTo(HttpStatusCode.Created);
        var committed = await CommitAsync(owner, Branch(database, "another"));
        await Assert.That(committed.Status).IsEqualTo(HttpStatusCode.OK);
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

    [Test]
    public async Task A_commit_aggregates_all_staged_branches_and_cleans_every_copy_when_refused()
    {
        await using var api = await StartAsync();
        (await api.PutAsync(DocumentApi.NewId(), new { data = "x" })).Status.ShouldBe(HttpStatusCode.Created); // 12 bytes.
        var database = await api.TenantOfAsync();
        using var owner = api.Management.As(api.Tenant.User);
        var refused = await CommitAsync(owner, Branch(database, "one"), Branch(database, "two"));
        await IsQuotaAsync(refused);
        await Assert.That(refused.Op).IsEqualTo(1);
        await Assert.That(await DatabaseCountAsync(api)).IsEqualTo(1);
        var retry = await CommitAsync(owner, Branch(database, "one"));
        await Assert.That(retry.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await DatabaseCountAsync(api)).IsEqualTo(2);
    }

    [Test]
    public async Task Concurrent_multi_organization_commits_acquire_stripes_in_order_and_preserve_both_quotas()
    {
        await using var api = await StartAsync();
        (await api.PutAsync(DocumentApi.NewId(), new { data = "x" })).Status.ShouldBe(HttpStatusCode.Created);
        var first = await api.TenantOfAsync();
        using var owner = api.Management.As(api.Tenant.User);
        var organization = await api.Management.CreateOrganizationAsync("Other", owner);
        var project = await api.Management.CreateProjectAsync(organization, "Other", owner);
        var database = await api.Management.CreateDatabaseAsync(organization, project, "other", owner);
        var second = new TenantContext(organization, PlanTier.Hobby, project, database);
        using var data = await api.Management.CreateClientAsync(organization, "documents:read", "documents:write");
        var path = $"/v1/projects/{project}/databases/other/collections";
        (await DocumentApi.SendAsync(data, HttpMethod.Post, path, JsonContent.Create(new { name = "items" }))).Status.ShouldBe(HttpStatusCode.Created);
        (await DocumentApi.SendAsync(data, HttpMethod.Put, path + "/items/documents/by-key?id=" + DocumentApi.NewId(), JsonContent.Create(new { data = "x" })))
            .Status.ShouldBe(HttpStatusCode.Created);
        var results = await Task.WhenAll(
            CommitAsync(owner, Branch(first, "one"), Branch(second, "one")),
            CommitAsync(owner, Branch(second, "two"), Branch(first, "two")));
        await Assert.That(results.Count(result => result.Status == HttpStatusCode.OK)).IsEqualTo(1);
        await IsQuotaAsync(results.Single(result => result.Status != HttpStatusCode.OK));
        await Assert.That(await DatabaseCountAsync(api)).IsEqualTo(4);
    }

    [Test]
    public async Task A_commit_can_replace_a_full_database_with_its_branch_without_increasing_usage()
    {
        var hook = new UsageCommitHook(storageLimit: _ => 32);
        await using var api = await DocumentApi.StartAsync(services: hook.Install);
        (await api.PutAsync(DocumentApi.NewId(), new { data = new string('x', 21) })).Status.ShouldBe(HttpStatusCode.Created);
        var database = await api.TenantOfAsync();
        using var owner = api.Management.As(api.Tenant.User);
        var body = new
        {
            id = EntityIds.New().ToString(),
            ops = new object[]
            {
                Branch(database, "replacement"),
                new { op = "delete_database", organization = database.OrganizationId, project = database.ProjectId, database = database.DatabaseId },
            },
        };
        using var staged = new UsageCommitHook.Gate(fail: false);
        var arrivals = 0;
        var retryQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hook.AfterQuotaBytes = (target, cancellationToken) => target.DatabaseId != database.DatabaseId
            ? staged.WaitAsync(cancellationToken) : ValueTask.CompletedTask;
        hook.BeforeQuota = _ =>
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                retryQueued.TrySetResult();
            }

            return ValueTask.CompletedTask;
        };
        using var firstContent = JsonContent.Create(body);
        using var retryContent = JsonContent.Create(body);
        var uri = new Uri("/v1/management/commits", UriKind.Relative);
        var committing = owner.PostAsync(uri, firstContent);
        await staged.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var retrying = owner.PostAsync(uri, retryContent);
        await retryQueued.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(retrying.IsCompleted).IsFalse();
        staged.Dispose();
        using var first = await committing;
        using var retry = await retrying;
        await Assert.That((first.StatusCode, retry.StatusCode)).IsEqualTo((HttpStatusCode.OK, HttpStatusCode.OK));
        await Assert.That(first.Headers.Contains(CommitEndpoint.ReplayedHeader)).IsFalse();
        await Assert.That(retry.Headers.GetValues(CommitEndpoint.ReplayedHeader)).IsEquivalentTo(["true"]);
        await Assert.That(await retry.Content.ReadAsStringAsync()).IsEqualTo(await first.Content.ReadAsStringAsync());
        await Assert.That(await DatabaseCountAsync(api)).IsEqualTo(1);
        await Assert.That((await DocumentApi.SendAsync(owner, HttpMethod.Get, Managed(database))).Status).IsEqualTo(HttpStatusCode.NotFound);
        var listed = await DocumentApi.SendAsync(owner, HttpMethod.Get, Managed(database)[..Managed(database).LastIndexOf('/')]);
        await Assert.That(listed.Json.GetProperty("items")[0].GetProperty("name").GetString()).IsEqualTo("replacement");
    }

    private static Task<DocumentApi> StartAsync() => DocumentApi.StartAsync(services: services => services.AddSingleton<IStorageQuota>(provider =>
        new StorageQuota(provider.GetRequiredService<IStorageEngine>(), provider.GetRequiredService<ITenantDirectory>(), _ => 32)));

    private static object Branch(TenantContext database, string name) =>
        new { op = "branch_database", organization = database.OrganizationId, project = database.ProjectId, database = database.DatabaseId, name };

    private static Task<ApiResponse> CommitAsync(HttpClient owner, params object[] ops) => DocumentApi.SendAsync(owner, HttpMethod.Post,
        "/v1/management/commits", JsonContent.Create(new { id = EntityIds.New().ToString(), ops }));

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
        public ValueTask<StorageQuotaLease> EnterAsync(TenantContext database, IDatabaseStore store, CancellationToken cancellationToken) =>
            inner.EnterAsync(database, store, cancellationToken);

        public ValueTask<StorageQuotaGroupLease> EnterOrganizationsAsync(IReadOnlyList<string> organizationIds, CancellationToken cancellationToken) =>
            inner.EnterOrganizationsAsync(organizationIds, cancellationToken);

        public ValueTask<long> ReadBytesAsync(TenantContext database, CancellationToken cancellationToken) =>
            throw new QueryCapacityException("injected accounting capacity");

        public void Dispose() => inner.Dispose();
    }
}
