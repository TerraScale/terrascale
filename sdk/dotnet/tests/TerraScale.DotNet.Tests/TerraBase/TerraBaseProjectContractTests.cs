using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseProjectContractTests
{

    [Test]
    public async Task Canonical_lifecycle_uses_project_authority_database_names_and_home_region()
    {
        var requests = new List<(string Method, string Path, string Query, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            await Assert.That(request.Headers.Authorization!.Scheme).IsEqualTo("Bearer");
            await Assert.That(request.Headers.Authorization.Parameter).IsEqualTo("project-user-token");
            await Assert.That(request.RequestUri!.Host).IsEqualTo("api.terrascale.tech");
            await Assert.That(request.Headers.GetValues("X-TerraScale-Region-Pin").Single()).IsEqualTo("sjc");
            await Assert.That(request.Headers.Contains("X-TerraBase-User-Id")).IsFalse();
            requests.Add((request.Method.Method, request.RequestUri.AbsolutePath, request.RequestUri.Query,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : TerraBaseIntegrationFixture.Json(TerraBaseIntegrationFixture.Read(request.RequestUri.AbsolutePath.EndsWith("/branches", StringComparison.Ordinal) ? "branch.json" : request.Method == HttpMethod.Get && request.RequestUri.Query.Length > 0 ? "databases.json" : "database.json"));
        });
        using var http = new HttpClient(handler);
        var client = TerraBaseIntegrationFixture.Client(http);
        var project = client.Project(TerraBaseIntegrationFixture.ProjectId);
        var created = await project.CreateDatabaseAsync("app", "gru");
        var page = await project.ListDatabasesAsync(after: created.Id, limit: 1);
        var found = await project.GetDatabaseAsync("app");
        await project.DeleteDatabaseAsync("app");
        var branch = await client.Database(TerraBaseIntegrationFixture.ProjectId, "app").BranchAsync("copy");

        await Assert.That(created.Region).IsEqualTo("sjc");
        await Assert.That(created.HomeRegion).IsEqualTo("gru");
        await Assert.That(found.Id).IsEqualTo(created.Id);
        await Assert.That(page.Next).IsEqualTo(created.Id);
        await Assert.That(branch.BranchedFrom).IsEqualTo(created.Id);
        await Assert.That(branch.Name).IsEqualTo("copy");
        await Assert.That(requests.Select(r => r.Method)).IsEquivalentTo(["POST", "GET", "GET", "DELETE", "POST"]);
        await Assert.That(requests[2].Path).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app");
        await Assert.That(requests[1].Query).IsEqualTo($"?after={created.Id}&limit=1");
        await Assert.That(requests[0].Body).IsEqualTo("{\"name\":\"app\",\"homeRegion\":\"gru\"}");
        await Assert.That(requests[4].Body).IsEqualTo("{\"name\":\"copy\"}");
    }

    [Test]
    public async Task Identity_conditioned_branch_and_delete_send_the_expected_id_segment()
    {
        var id = "01ARZ3NDEKTSV4RRFFQ69G5FA2";
        var requests = new List<(string Method, string Path, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : TerraBaseIntegrationFixture.Json(TerraBaseIntegrationFixture.Read("branch.json"), HttpStatusCode.Created);
        });
        using var http = new HttpClient(handler);
        var database = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var branched = await database.BranchIfIdAsync("copy", id);
        await database.DeleteIfIdAsync(id);

        await Assert.That(branched.Name).IsEqualTo("copy");
        await Assert.That(requests.Select(r => r.Method)).IsEquivalentTo(["POST", "DELETE"]);
        await Assert.That(requests[0].Path).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/branches/if-id/{id}");
        await Assert.That(requests[0].Body).IsEqualTo("{\"name\":\"copy\"}");
        await Assert.That(requests[1].Path).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/if-id/{id}");
    }

    [Test]
    public async Task Usage_preserves_int64_totals_periods_and_database_identity_without_manufacturing_billing()
    {
        var queries = new List<string>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            queries.Add(request.RequestUri!.Query);
            return Task.FromResult(TerraBaseIntegrationFixture.Json(TerraBaseIntegrationFixture.Read(
                request.RequestUri.AbsolutePath.Contains("/databases/", StringComparison.Ordinal) ? "database-usage.json" : "project-usage.json")));
        });
        using var http = new HttpClient(handler);
        var client = TerraBaseIntegrationFixture.Client(http);
        var from = DateTimeOffset.Parse("2026-10-12T00:00:00+03:00");
        var to = from.AddHours(1);
        var project = await client.Project(TerraBaseIntegrationFixture.ProjectId).GetUsageAsync("hour", from, to);
        var database = await client.Database(TerraBaseIntegrationFixture.ProjectId, "app").GetUsageAsync();

        await Assert.That(project.StorageBytes).IsEqualTo(9007199254740993L);
        await Assert.That(database.DatabaseId).IsEqualTo(project.Items[0].DatabaseId);
        await Assert.That(project.Items[0].Series).IsEmpty();
        await Assert.That(database.Series[0].Writes).IsEqualTo(3);
        await Assert.That(database.Reads).IsEqualTo(22);
        await Assert.That(database.Period.Reads).IsEqualTo(12);
        await Assert.That(queries[0]).IsEqualTo("?granularity=hour&from=2026-10-12T00%3A00%3A00.0000000%2B03%3A00&to=2026-10-12T01%3A00%3A00.0000000%2B03%3A00");
        await Assert.That(queries[1]).IsEqualTo("");
    }

}
