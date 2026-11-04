using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase.Sql;

public sealed class TerraBaseAgentDatabaseTests
{
    private const string Project = TerraBaseIntegrationFixture.ProjectId;
    private const string Database = "01JA0000000000000000000004";
    private static readonly string ApiKey = "01JA00000000000000000000K8_" + new string('b', 52);

    [Test]
    public async Task Agent_database_lifecycle_and_scoped_key_match_recorded_contracts()
    {
        var fixtures = new Queue<string>(["create-with-ttl", "fork-now", "fork-point-in-time", "set-ttl", "clear-ttl", "delete", "scoped-key"]);
        var exchanges = 0;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/terrascale/oauth/token")
            {
                exchanges++;
                await AssertBasicAsync(request);
                await Assert.That(await request.Content!.ReadAsStringAsync(token)).IsEqualTo($"grant_type=client_credentials&project={Project}");
                return TerraBaseIntegrationFixture.Json("""{"access_token":"agent-project-token","expires_in":900}""");
            }
            return await ReplayAsync(fixtures.Dequeue(), request, token);
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { Region = "gru", ApiKey = ApiKey });

        var created = await client.CreateDatabaseAsync(Project, "agent", ttlSeconds: 3600);
        var fork = await client.ForkDatabaseAsync(Project, Database, "agent-fork", ttlSeconds: 3600);
        var restored = await client.ForkDatabaseAsync(Project, Database, "agent-restore", DateTimeOffset.Parse("2026-11-04T11:30:00+00:00", System.Globalization.CultureInfo.InvariantCulture), 3600);
        var updated = await client.SetDatabaseTtlAsync(Project, Database, 3600);
        var permanent = await client.ClearDatabaseTtlAsync(Project, Database);
        await client.DeleteDatabaseAsync(Project, Database);
        var key = await client.CreateScopedKeyAsync(Database, "read_write", Project, 3600);

        await Assert.That(created.Id).IsEqualTo(Database);
        await Assert.That(created.ExpiresAt).IsEqualTo(DateTimeOffset.Parse("2026-11-04T13:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That((created.Region, created.HomeRegion)).IsEqualTo(("gru", "gru"));
        await Assert.That((fork.Name, fork.BranchedFrom, restored.Name, restored.BranchedFrom)).IsEqualTo(("agent-fork", Database, "agent-restore", Database));
        await Assert.That((fork.ExpiresAt, restored.ExpiresAt, updated.ExpiresAt)).IsEqualTo((created.ExpiresAt, created.ExpiresAt, created.ExpiresAt));
        await Assert.That(permanent.ExpiresAt).IsNull();
        await Assert.That((key.ProjectId, key.Database, key.Access, key.ExpiresAt)).IsEqualTo((Project, Database, "read_write", created.ExpiresAt!.Value));
        using var recorded = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("agent/scoped-key.json"));
        await Assert.That(key.Key).IsEqualTo(recorded.RootElement.GetProperty("response").GetProperty("body").GetProperty("key").GetString());
        await Assert.That(key.ToString().Contains(key.Key, StringComparison.Ordinal)).IsFalse();
        _ = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { Region = "gru", ApiKey = key.Key });
        await Assert.That(exchanges).IsEqualTo(1);
        await Assert.That(fixtures.Count).IsEqualTo(0);

        using var providerHandler = new TerraBaseTestHandler((request, token) => ReplayAsync("create-with-ttl", request, token));
        using var providerHttp = new HttpClient(providerHandler);
        var providerClient = new TerraBaseSqlClient(providerHttp, new TerraBaseSqlClientOptions
        {
            Region = "gru", AccessTokenProvider = _ => ValueTask.FromResult("agent-project-token"),
        });
        await Assert.That((await providerClient.CreateDatabaseAsync(Project, "agent", ttlSeconds: 3600)).Id).IsEqualTo(Database);
    }

    [Test]
    public async Task Scoped_key_authority_refusal_is_typed_and_never_retried()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((request, token) =>
        {
            calls++;
            return ReplayAsync("scoped-key-refused", request, token);
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { Region = "gru", ApiKey = ApiKey });

        var error = await Assert.ThrowsAsync<TerraBaseForbiddenException>(() => client.CreateScopedKeyAsync(Database, "read_write", Project, 3600));

        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(error.ErrorCode).IsEqualTo("forbidden");
        await Assert.That(calls).IsEqualTo(1);
    }

    private static async Task<HttpResponseMessage> ReplayAsync(string fixture, HttpRequestMessage request, CancellationToken token)
    {
        using var json = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("agent/" + fixture + ".json"));
        var expected = json.RootElement.GetProperty("request");
        await Assert.That(request.Method.Method).IsEqualTo(expected.GetProperty("method").GetString());
        await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(expected.GetProperty("path").GetString());
        await Assert.That(request.RequestUri.Host).IsEqualTo(expected.TryGetProperty("origin", out var origin) ? new Uri(origin.GetString()!).Host : "api.terrascale.tech");
        foreach (var header in expected.GetProperty("headers").EnumerateObject())
        {
            var values = header.Name == "Content-Type" ? request.Content!.Headers.GetValues(header.Name) : request.Headers.GetValues(header.Name);
            await Assert.That(values.Single()).IsEqualTo(header.Value.GetString());
        }
        if (expected.GetProperty("body").ValueKind == JsonValueKind.Null)
            await Assert.That(request.Content).IsNull();
        else
            await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)), JsonNode.Parse(expected.GetProperty("body").GetRawText()))).IsTrue();
        if (request.RequestUri.Host == "auth.terrascale.tech") await AssertBasicAsync(request);
        else
        {
            await Assert.That(request.Headers.Authorization!.Scheme).IsEqualTo("Bearer");
            await Assert.That(request.Headers.Authorization.Parameter).IsEqualTo("agent-project-token");
            await Assert.That(request.Headers.GetValues(TerraBaseRegion.PinHeader).Single()).IsEqualTo("gru");
        }
        var response = json.RootElement.GetProperty("response");
        return response.GetProperty("status").GetInt32() == 204
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : TerraBaseIntegrationFixture.Json(response.GetProperty("body").GetRawText(), (HttpStatusCode)response.GetProperty("status").GetInt32());
    }

    private static async Task AssertBasicAsync(HttpRequestMessage request)
    {
        await Assert.That(request.Headers.Authorization!.Scheme).IsEqualTo("Basic");
        await Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!))).IsEqualTo($"{ApiKey[..26]}:{ApiKey}");
    }
}
