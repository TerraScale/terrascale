using System.Net;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseFunctionCatalogTests
{
    private const string Function = """{"name":"hello","source":"function handler(event) { return event; }","cron":"* * * * *","createdAt":"2026-10-26T00:00:00Z","updatedAt":"2026-10-26T01:00:00Z"}""";

    [Test]
    public async Task Catalog_matches_pinned_routes_and_roundtrips_replacement_fields()
    {
        using var manifest = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("functions.json"));
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            var route = manifest.RootElement.GetProperty("operations")[calls++];
            await Assert.That(request.Method.Method).IsEqualTo(route.GetProperty("method").GetString());
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(route.GetProperty("path").GetString()!
                .Replace("{project}", TerraBaseIntegrationFixture.ProjectId, StringComparison.Ordinal)
                .Replace("{database}", "app", StringComparison.Ordinal).Replace("{function}", "hello", StringComparison.Ordinal));
            await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
            await Assert.That(request.Headers.Contains("Idempotency-Key")).IsFalse();
            await Assert.That(route.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()!).ToArray())
                .IsEquivalentTo(new[] { "sql:read", "sql:write" });
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Put)
            {
                await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
                await Assert.That(body.RootElement.GetProperty("source").GetString()).IsEqualTo("function handler(event) { return event; }");
                await Assert.That(body.RootElement.GetProperty("cron").GetString()).IsEqualTo("* * * * *");
            }
            return TerraBaseIntegrationFixture.Json(calls == 1
                ? """[{"name":"hello","createdAt":"2026-10-26T00:00:00Z","updatedAt":"2026-10-26T01:00:00Z"}]""" : Function);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var listed = await db.ListFunctionsAsync();
        await Assert.That(listed.Single().Source).IsNull();
        var deployed = await db.DeployFunctionAsync("hello", new("function handler(event) { return event; }", "* * * * *"));
        await Assert.That(deployed.Cron).IsEqualTo("* * * * *");
        await Assert.That(deployed.CreatedAt).IsEqualTo(DateTimeOffset.Parse("2026-10-26T00:00:00Z"));
        var fetched = await db.GetFunctionAsync("hello");
        await Assert.That(fetched.Source).IsEqualTo(deployed.Source);
        await db.DeleteFunctionAsync("hello");
        await Assert.That(calls).IsEqualTo(4);
    }

}
