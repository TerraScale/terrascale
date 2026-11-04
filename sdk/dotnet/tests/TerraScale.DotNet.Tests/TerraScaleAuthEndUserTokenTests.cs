using System.Net;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthEndUserTokenTests
{
    private const string Project = "01JA0000000000000000000001";
    private const string User = "01JA0000000000000000000002";
    private static readonly string Key = "01JA0000000000000000000003" + "_" + new string('a', 52);
    private const string Token = "fixture-sensitive-token-canary";
    private const string Success = """{"access_token":"fixture-sensitive-token-canary","token_type":"Bearer","expires_in":900}""";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Exact_backend_key_route_and_only_supported_claims_are_sent(bool includeRoles)
    {
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "contracts/auth/end-user-tokens.json")));
        var operation = contract.RootElement.GetProperty("operation");
        using var routes = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "contracts/auth/routes.json")));
        var route = routes.RootElement.GetProperty("operations").EnumerateArray()
            .Single(item => item.GetProperty("operation_id").GetString() == operation.GetProperty("operationId").GetString());
        await Assert.That(route.GetProperty("method").GetString()).IsEqualTo(operation.GetProperty("method").GetString());
        await Assert.That(route.GetProperty("path").GetString()).IsEqualTo(operation.GetProperty("path").GetString());
        await Assert.That(route.GetProperty("sdk_method").GetString()).IsEqualTo("Projects.MintEndUserTokenAsync");
        await Assert.That(route.GetProperty("contract").GetString()).IsEqualTo("end-user-tokens.json");
        using var handler = new Handler(async (request, token) =>
        {
            await Assert.That(request.Method.Method).IsEqualTo(operation.GetProperty("method").GetString());
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(operation.GetProperty("path").GetString()!.Replace("{project}", Project, StringComparison.Ordinal));
            await Assert.That(request.Headers.Authorization!.Scheme).IsEqualTo("Bearer");
            await Assert.That(request.Headers.Authorization.Parameter).IsEqualTo(Key);
            await Assert.That(request.Headers.Contains("Idempotency-Key")).IsFalse();
            await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            await Assert.That(body.RootElement.GetProperty("user_id").GetString()).IsEqualTo(User);
            await Assert.That(body.RootElement.EnumerateObject().Count()).IsEqualTo(includeRoles ? 2 : 1);
            if (includeRoles)
            {
                var claims = body.RootElement.GetProperty("claims");
                await Assert.That(claims.EnumerateObject().Count()).IsEqualTo(1);
                await Assert.That(claims.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray())
                    .IsEquivalentTo(new[] { "reader", "Reader", "reader" });
            }
            return Reply(Success);
        });
        using var http = new HttpClient(handler);
        var result = await Client(http).Projects.MintEndUserTokenAsync(Key, Project, User, includeRoles ? ["reader", "Reader", "reader"] : null);
        await Assert.That(result.AccessToken).IsEqualTo(Token);
        await Assert.That(result.ExpiresIn).IsEqualTo(900L);
        await Assert.That(result.ToString()).DoesNotContain(Token);
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new() { BaseAddress = new("https://auth.example.test"), InstanceSlug = "terrascale" });
    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request, token); }
    }
}
