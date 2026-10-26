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

    [Test]
    public async Task Producer_role_bounds_and_canonical_ids_are_checked_without_echoing_inputs()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Reply(Success)));
        using var http = new HttpClient(handler);
        var api = Client(http).Projects;
        foreach (var id in new[] { "bad", Project.ToLowerInvariant(), "81JA0000000000000000000001", "proj_" + Project })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => api.MintEndUserTokenAsync(Key, id, User));
            await Assert.ThrowsAsync<ArgumentException>(() => api.MintEndUserTokenAsync(Key, Project, id));
        }
        foreach (var roles in new string[][] { [""], [" "], ["role-secret-canary\n"], [new string('x', 129)], [null!], Enumerable.Repeat("reader", 65).ToArray() })
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => api.MintEndUserTokenAsync(Key, Project, User, roles));
            await Assert.That(error!.ToString()).DoesNotContain("role-secret-canary");
        }
        foreach (var key in new[] { "fixture-secret-key-canary", Project + "_" + new string('a', 51), Project + "_" + new string('A', 52) })
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => api.MintEndUserTokenAsync(key, Project, User));
            await Assert.That(error!.ToString()).DoesNotContain(key);
        }
        await Assert.That(handler.Calls).IsEqualTo(0);
        await api.MintEndUserTokenAsync(Key, Project, User, Enumerable.Repeat(new string('x', 128), 64).ToArray());
        await api.MintEndUserTokenAsync(Key, Project, User, []);
        await Assert.That(handler.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Serialized_UTF8_body_cannot_exceed_the_producer_limit()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Reply(Success)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Client(http).Projects.MintEndUserTokenAsync(Key, Project, User,
            Enumerable.Repeat(new string('\u00e9', 128), 64).ToArray()));
        await Assert.That(error!.ToString()).DoesNotContain(Key);
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(400, "invalid_request")]
    [Arguments(401, "unauthorized")]
    [Arguments(429, "rate_limited")]
    [Arguments(503, "temporarily_unavailable")]
    public async Task Both_error_envelopes_remain_safe_and_mutations_never_retry(int status, string code)
    {
        using var handler = new Handler((_, _) =>
        {
            var raw = status == 429 ? JsonSerializer.Serialize(new { error = code, error_description = Key }) : JsonSerializer.Serialize(new { error = new { code, message = Token } });
            var response = Reply(raw, (HttpStatusCode)status);
            response.Headers.TryAddWithoutValidation("Retry-After", "7");
            response.Headers.TryAddWithoutValidation("X-Request-ID", User);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Projects.MintEndUserTokenAsync(Key, Project, User));
        await Assert.That(error!.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(error.ErrorCode).IsEqualTo(code);
        await Assert.That(error.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(7));
        await Assert.That(error.RequestId).IsEqualTo(User);
        await Assert.That(error.ToString()).DoesNotContain(Key);
        await Assert.That(error.ToString()).DoesNotContain(Token);
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("{}", 200, "application/json")]
    [Arguments("{\"access_token\":null,\"token_type\":\"Bearer\",\"expires_in\":900}", 200, "application/json")]
    [Arguments("{\"access_token\":\"fixture-sensitive-token-canary\",\"token_type\":\"Bearer\",\"expires_in\":901}", 200, "application/json")]
    [Arguments("{\"access_token\":\"fixture-sensitive-token-canary\",\"token_type\":\"Bearer\",\"expires_in\":0}", 200, "application/json")]
    [Arguments("{\"access_token\":\"fixture-sensitive-token-canary\",\"token_type\":\"Basic\",\"expires_in\":900}", 200, "application/json")]
    [Arguments(Success, 200, "text/html")]
    [Arguments(Success, 201, "application/json")]
    [Arguments(Success, 204, "application/json")]
    [Arguments(Success, 302, "application/json")]
    public async Task Malformed_or_wrong_status_success_is_refused_safely(string body, int status, string media)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, media) }));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Projects.MintEndUserTokenAsync(Key, Project, User));
        await Assert.That(error!.ToString()).DoesNotContain(Token);
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Token_encoding_and_size_are_bounded_without_raw_diagnostics()
    {
        foreach (var token in new[] { new string('x', 8193), "\u00e9", "\nsecret-canary" })
        {
            using var handler = new Handler((_, _) => Task.FromResult(Reply(JsonSerializer.Serialize(new { access_token = token, token_type = "Bearer", expires_in = 1 }))));
            using var http = new HttpClient(handler);
            var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Projects.MintEndUserTokenAsync(Key, Project, User));
            await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
            await Assert.That(error.ToString()).DoesNotContain("secret-canary");
        }
    }

    [Test]
    public async Task Caller_cancellation_is_honored_before_and_during_the_single_request()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Reply(Success); });
        using var http = new HttpClient(handler);
        var api = Client(http).Projects;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => api.MintEndUserTokenAsync(Key, Project, User, cancellationToken: canceled.Token));
        await Assert.That(handler.Calls).IsEqualTo(0);
        using var cancel = new CancellationTokenSource();
        var pending = api.MintEndUserTokenAsync(Key, Project, User, cancellationToken: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
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
