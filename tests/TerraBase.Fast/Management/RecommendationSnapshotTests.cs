using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Errors;
using TerraBase.Features.Management.Queries;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class RecommendationSnapshotTests
{
    [Test]
    public async Task Recommendation_reads_and_apply_wait_for_a_committed_flush_to_remove_its_pending_counts()
    {
        var hook = new UsageCommitHook((collection, _, _) => collection.EndsWith("/query_fields", StringComparison.Ordinal));
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(), services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        var recorder = api.Server.Services.GetRequiredService<QueryFieldRecorder>();
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        using var firstRun = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/sql"), new { sql = "SELECT * FROM people WHERE email = 'a'" });
        firstRun.EnsureSuccessStatusCode();

        // The catalog now contains the batch, but the recorder still holds it as pending.
        using var gate = hook.PauseAfterCommit();
        var flush = recorder.FlushAsync(CancellationToken.None).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var nextRun = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/sql"), new { sql = "SELECT * FROM people WHERE email = 'b'" });
        nextRun.EnsureSuccessStatusCode();
        var snapshot = recorder.ReadAsync(tenant, CancellationToken.None).AsTask();
        var read = api.Client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(database.Path + "/index-recommendations"));
        var apply = api.Client.PostAsync(ManagementApi.Uri(database.Path + "/index-recommendations/people:email/apply"), null);
        await Assert.That(snapshot.IsCompleted).IsFalse();

        gate.Dispose();
        await flush.WaitAsync(TimeSpan.FromSeconds(10));
        var usages = await snapshot.WaitAsync(TimeSpan.FromSeconds(10)) switch
        {
            List<QueryFieldUsage> values => values,
            Unavailable => throw new InvalidOperationException("the query usage catalog was unavailable"),
        };
        await Assert.That(usages.Single().Queries).IsEqualTo(2);
        // Apply may win the race with the list and remove the recommendation by covering the field.
        var items = (await read).GetProperty("items");
        if (items.GetArrayLength() > 0)
        {
            await Assert.That(items[0].GetProperty("queries").GetInt64()).IsEqualTo(2);
        }

        using var applied = await apply;
        await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await recorder.FlushAsync(CancellationToken.None);
        var saved = await recorder.ReadAsync(tenant, CancellationToken.None) switch
        {
            List<QueryFieldUsage> values => values,
            Unavailable => throw new InvalidOperationException("the query usage catalog was unavailable"),
        };
        await Assert.That(saved.Single().Queries).IsEqualTo(2);
    }

    [Test]
    public async Task A_query_field_flush_that_fails_after_its_commit_is_not_counted_again()
    {
        var hook = new UsageCommitHook((collection, _, _) => collection.EndsWith("/query_fields", StringComparison.Ordinal));
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(), services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        var recorder = api.Server.Services.GetRequiredService<QueryFieldRecorder>();
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        using var firstRun = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/sql"), new { sql = "SELECT * FROM people WHERE email = 'a'" });
        firstRun.EnsureSuccessStatusCode();

        // The write commits, then the flush fails as if disposing the transaction had thrown.
        using (var gate = hook.PauseAfterCommit(fail: true))
        {
            var flush = recorder.FlushAsync(CancellationToken.None).AsTask();
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            gate.Dispose();
            await Assert.ThrowsAsync<IOException>(() => flush);
        }

        var recommendations = await api.Client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(database.Path + "/index-recommendations"));
        using var nextRun = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/sql"), new { sql = "SELECT * FROM people WHERE email = 'b'" });
        nextRun.EnsureSuccessStatusCode();
        await recorder.FlushAsync(CancellationToken.None);

        await Assert.That(recommendations.GetProperty("items")[0].GetProperty("queries").GetInt64()).IsEqualTo(1);
        // Nothing is pending after the second flush: this is what the catalog saved.
        var saved = await recorder.ReadAsync(tenant, CancellationToken.None) switch
        {
            List<QueryFieldUsage> values => values,
            Unavailable => throw new InvalidOperationException("the query usage catalog was unavailable"),
        };
        await Assert.That(saved.Single().Queries).IsEqualTo(2);
    }
}
