using System.Net;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseCollectionRulesTests
{
    [Test]
    public async Task Rules_match_the_pinned_public_contract_and_preserve_deny_by_default()
    {
        using var manifest = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("collection-rules.json"));
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            var route = manifest.RootElement.GetProperty("operations")[calls++];
            await Assert.That(request.Method.Method).IsEqualTo(route.GetProperty("method").GetString());
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(route.GetProperty("path").GetString()!
                .Replace("{project}", TerraBaseIntegrationFixture.ProjectId, StringComparison.Ordinal)
                .Replace("{database}", "app", StringComparison.Ordinal).Replace("{collection}", "users", StringComparison.Ordinal));
            await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
            await Assert.That(request.Headers.Contains("Idempotency-Key")).IsFalse();
            if (request.Method == HttpMethod.Get) return TerraBaseIntegrationFixture.Json("""{"rules":null}""");
            await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            await Assert.That(body.RootElement.GetProperty("owner").GetString()).IsEqualTo("/userId");
            await Assert.That(body.RootElement.GetProperty("read").GetProperty("public").GetBoolean()).IsFalse();
            await Assert.That(body.RootElement.GetProperty("write").GetProperty("roles").GetArrayLength()).IsEqualTo(0);
            return TerraBaseIntegrationFixture.Json("{\"rules\":" + body.RootElement.GetRawText() + "}");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var undeclared = await db.GetCollectionRulesAsync("users");
        await Assert.That(undeclared.Rules).IsNull();
        var set = await db.SetCollectionRulesAsync("users", new("/userId", new(true, false, ["reader"]), new(true, false, [])));
        await Assert.That(set.Rules!.Read.Roles).IsEquivalentTo(new[] { "reader" });
        await Assert.That(set.Rules.Write.Public).IsFalse();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    [Arguments(HttpStatusCode.BadRequest, "invalid_rules")]
    [Arguments(HttpStatusCode.Forbidden, "forbidden_scope")]
    [Arguments(HttpStatusCode.NotFound, "unknown_collection")]
    public async Task Refused_rule_replacements_are_not_replayed(HttpStatusCode status, string code)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json("{\"error\":{\"code\":\"" + code + "\",\"message\":\"refused\"}}", status));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var error = await Assert.ThrowsAsync<TerraBaseException>(() =>
            db.SetCollectionRulesAsync("users", new(null, new(false, false, []), new(false, false, []))));
        await Assert.That(error!.StatusCode).IsEqualTo(status);
        await Assert.That(error.ErrorCode).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Rule_calls_honor_precancellation_and_keep_the_caller_client_alive()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json("""{"rules":null}"""));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.GetCollectionRulesAsync("users", canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            db.SetCollectionRulesAsync("users", new(null, new(false, false, []), new(false, false, [])), canceled.Token));
        await Assert.That(calls).IsEqualTo(0);
        await db.GetCollectionRulesAsync("users");
        await Assert.That(calls).IsEqualTo(1);
    }
    [Test]
    [Arguments("{}")]
    [Arguments("null")]
    [Arguments("{\"rules\":{}}")]
    [Arguments("{\"rules\":{\"read\":null,\"write\":null}}")]
    public async Task Malformed_policy_is_not_an_implicit_deny_or_grant(string body)
    {
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(TerraBaseIntegrationFixture.Json(body)));
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => db.GetCollectionRulesAsync("users"));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    public async Task Oversized_policy_is_refused_before_dispatch_and_cancellation_keeps_precedence()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json("{\"rules\":null}"));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var oversized = new TerraBaseCollectionAccessRules(null, new(false, false, [new string('x', 17 * 1024)]), new(false, false, []));
        await Assert.ThrowsAsync<ArgumentException>(() => db.SetCollectionRulesAsync("users", oversized));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.SetCollectionRulesAsync("users", oversized, canceled.Token));
        await Assert.That(calls).IsEqualTo(0);
    }

}
