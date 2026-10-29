using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Qos;
using TerraBase.Testing;

namespace TerraBase.Slow.Documents;

public sealed class TextSearchBudgetTests
{
    private static readonly string[] BodyFields = ["body"];
    private static readonly string[] NativeSearch = ["/body", "native"];

    [Test]
    public async Task Native_fts_deadlines_return_invalid_query_for_search_and_assert_count_and_recover()
    {
        var clock = new SearchClock();
        await using var api = await DocumentApi.StartAsync(TerraBaseTestServer.UseProductionQueryBudget,
            services: services => services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock));
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { body = "native budget" })).Status.ShouldBe(HttpStatusCode.Created);
        using var owner = api.Management.As(api.Tenant.User);
        var indexes = $"/v1/management/organizations/{api.Tenant.OrganizationId}/projects/{api.Tenant.ProjectId}/databases/{(await api.TenantOfAsync()).DatabaseId}/indexes";
        using var created = await owner.PostAsJsonAsync(ManagementApi.Uri(indexes), new { name = "budget_text", collection = "items", kind = "text", fields = BodyFields });
        created.EnsureSuccessStatusCode();
        var filter = new { search = NativeSearch };
        clock.Enabled = true;
        var query = await api.QueryAsync(new { filter, count = true });
        await Assert.That((query.Status, query.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_query"));
        var count = await api.TransactAsync(new { op = "assert_count", collection = "items", filter, gte = 1, lte = 1 });
        await Assert.That((count.Status, count.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_query"));
        clock.Enabled = false;
        var recovered = await api.QueryAsync(new { filter });
        await Assert.That(recovered.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(recovered.Ids).IsEquivalentTo([id]);
    }

    private sealed class SearchClock : TimeProvider
    {
        private long _timestamp;
        public bool Enabled { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Enabled && QosContext.Current is not null
            ? Interlocked.Add(ref _timestamp, 1_000_000) : Volatile.Read(ref _timestamp);
    }
}
