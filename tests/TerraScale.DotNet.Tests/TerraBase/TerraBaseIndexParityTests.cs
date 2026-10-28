using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseIndexParityTests
{
    [Test]
    [Arguments("applied", 201)]
    [Arguments("already-applied", 200)]
    public async Task Recommendations_use_canonical_routes_and_preserve_outcomes(string status, int responseStatus)
    {
        var calls = new List<(string Method, string Path, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("project-user-token");
            return request.Method == HttpMethod.Get
                ? TerraBaseIntegrationFixture.Json("""{"items":[{"id":"items:title","collection":"items","fields":["title"],"reason":"unindexed","queries":9007199254740993}]}""")
                : TerraBaseIntegrationFixture.Json($$$"""{"status":"{{{status}}}","index":{"name":"items_title","collection":"items","fields":["title"],"state":"active"}}""", (HttpStatusCode)responseStatus);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var recommendations = await db.ListIndexRecommendationsAsync();
        var result = await db.ApplyIndexRecommendationAsync(recommendations.Items[0].Id);
        await Assert.That(recommendations.Items.Single().Queries).IsEqualTo(9007199254740993L);
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Index.Name).IsEqualTo("items_title");
        await Assert.That(calls.Select(c => c.Method)).IsEquivalentTo(["GET", "POST"]);
        await Assert.That(calls[0].Path).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/index-recommendations");
        await Assert.That(calls[1].Path).EndsWith("/index-recommendations/items%3Atitle/apply");
        await Assert.That(calls[1].Body).IsNull();
    }

    [Test]
    [Arguments(403, "forbidden_scope")]
    [Arguments(404, "not_found")]
    [Arguments(409, "name_taken")]
    public async Task Applying_failure_is_not_retried(int status, string code)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json($$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""", (HttpStatusCode)status));
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => TerraBaseIntegrationFixture.Client(http)
            .Database(TerraBaseIntegrationFixture.ProjectId, "app").ApplyIndexRecommendationAsync("items:title"));
        await Assert.That(error!.ErrorCode).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(1);
    }
}
