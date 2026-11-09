using System.Diagnostics;
using System.Net;
using TerraBase.Features.Management.Usage;
using TerraBase.Primitives;

namespace TerraBase.Fast.Management;

public sealed class UsageDeadlineTests
{
    private sealed class PastBudget : TimeProvider
    {
        private readonly long _step = (long)((UsageService.Deadline + TimeSpan.FromSeconds(1)).TotalSeconds * Stopwatch.Frequency);
        private long _calls;

        public override long GetTimestamp() => Stopwatch.GetTimestamp() + Interlocked.Increment(ref _calls) * _step;
    }

    [Test]
    public async Task A_usage_request_past_its_deadline_answers_503_capacity_with_retry_after()
    {
        await using var api = await ManagementApi.StartAsync(time: new PastBudget());
        var database = await api.CreateManagedDatabaseAsync();
        using var client = api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "databases:read";
        }));
        using var response = await client.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/usage"),
            TestContext.Current!.Execution.CancellationToken);
        await ErrorAssert.ErrorAsync(response, HttpStatusCode.ServiceUnavailable, "capacity", "usage exceeded its execution budget; retry");
        await Assert.That(response.Headers.RetryAfter).IsNotNull();
    }
}
