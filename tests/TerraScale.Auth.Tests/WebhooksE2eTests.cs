namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Auth.Store;
using TerraScale.Auth.Webhooks;

/// <summary>Outbound webhooks and the audit-event query API, end to end:
/// real app over the embedded engine delivering to a real local HTTP listener.</summary>
[NotInParallel]
public sealed class WebhooksE2eTests
{
    [Test]
    public async Task DeliversMatchingAuditEventSignedWithHmac()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        await using var sink = WebhookSink.Start("match", "nomatch");

        using var created = PostJson($"/v1/{slug}/webhooks", apiKey, new { url = sink.BaseUrl + "/match", events = new[] { "user.*" } });
        var createdResponse = await service.Http.SendAsync(created);
        await Assert.That(createdResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var createdBody = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secret = createdBody.GetProperty("signing_secret").GetString()!;

        using var nomatch = PostJson($"/v1/{slug}/webhooks", apiKey, new { url = sink.BaseUrl + "/nomatch", events = new[] { "org.created" } });
        await Assert.That((await service.Http.SendAsync(nomatch)).StatusCode).IsEqualTo(HttpStatusCode.OK);

        var marker = "marker-" + Guid.NewGuid().ToString("N")[..8];
        var auditEvent = await service.Store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + Guid.NewGuid().ToString("N")[..12],
            InstanceId = instanceId,
            Type = "user.profile_changed",
            Data = new Dictionary<string, object> { ["marker"] = marker },
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        var delivery = await WaitForDeliveryAsync(sink, d => d.Body.Contains(auditEvent.Id));

        var payload = JsonDocument.Parse(delivery.Body).RootElement;
        await Assert.That(payload.GetProperty("id").GetString()).IsEqualTo(auditEvent.Id);
        await Assert.That(payload.GetProperty("type").GetString()).IsEqualTo("user.profile_changed");
        await Assert.That(payload.GetProperty("created_at").GetInt64()).IsEqualTo(auditEvent.CreatedAt);
        await Assert.That(payload.GetProperty("data").GetProperty("marker").GetString()).IsEqualTo(marker);
        await Assert.That(delivery.Headers.GetValueOrDefault("Content-Type", "").StartsWith("application/json")).IsTrue();
        await Assert.That(delivery.Headers.GetValueOrDefault("X-TsAuth-Event")).IsEqualTo("user.profile_changed");

        var timestamp = delivery.Headers.GetValueOrDefault("X-TsAuth-Timestamp", "");
        await Assert.That(long.TryParse(timestamp, out var unix) && Math.Abs(unix - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) <= 60).IsTrue();

        var signature = delivery.Headers.GetValueOrDefault("X-TsAuth-Signature", "");
        await Assert.That(signature).IsEqualTo("sha256=" + HexHmac(secret, timestamp, delivery.Body));
        await Assert.That(signature).IsNotEqualTo("sha256=" + HexHmac("wrong-secret", timestamp, delivery.Body));

        // Once the matching endpoint got its delivery, the non-matching one must have nothing.
        bool nonMatchingReceived;
        lock (sink.Deliveries)
            nonMatchingReceived = sink.Deliveries.Any(d => d.Path.StartsWith("/nomatch", StringComparison.Ordinal));
        await Assert.That(nonMatchingReceived).IsFalse();
    }

    [Test]
    public async Task TestEndpointDeliversWebhookTestEventToWildcardEndpoint()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (_, slug, apiKey) = await NewInstanceWithKeyAsync();
        await using var sink = WebhookSink.Start("wild");

        using var created = PostJson($"/v1/{slug}/webhooks", apiKey, new { url = sink.BaseUrl + "/wild", events = new[] { "*" } });
        var createdResponse = await service.Http.SendAsync(created);
        await Assert.That(createdResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var endpointId = (await createdResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        using var test = PostJson($"/v1/{slug}/webhooks/{endpointId}/test", apiKey, new { });
        var testResponse = await service.Http.SendAsync(test);
        await Assert.That(testResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var testBody = await testResponse.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(testBody.GetProperty("queued").GetBoolean()).IsTrue();

        var delivery = await WaitForDeliveryAsync(sink, d => d.Headers.GetValueOrDefault("X-TsAuth-Event") == "webhook.test");
        var payload = JsonDocument.Parse(delivery.Body).RootElement;
        await Assert.That(payload.GetProperty("type").GetString()).IsEqualTo("webhook.test");
        await Assert.That(payload.GetProperty("data").GetProperty("endpoint_id").GetString()).IsEqualTo(endpointId);
    }

    [Test]
    public async Task AuditQueryApiRequiresAuthAndReturnsEventsNewestFirst()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();

        var older = await service.Store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + Guid.NewGuid().ToString("N")[..12],
            InstanceId = instanceId,
            Type = "user.audit_query",
            Data = new Dictionary<string, object> { ["n"] = 1 },
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await Task.Delay(TimeSpan.FromSeconds(1.1)); // distinct unix-second CreatedAt for the newest-first assertion
        var newer = await service.Store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + Guid.NewGuid().ToString("N")[..12],
            InstanceId = instanceId,
            Type = "user.audit_query",
            Data = new Dictionary<string, object> { ["n"] = 2 },
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        await Assert.That((await service.Http.GetAsync($"/v1/{slug}/audit-events")).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var wrongSlug = Get($"/v1/{slug}-wrong/audit-events", apiKey);
        await Assert.That((await service.Http.SendAsync(wrongSlug)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        using var query = Get($"/v1/{slug}/audit-events?limit=10", apiKey);
        var response = await service.Http.SendAsync(query);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var events = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(events.GetArrayLength()).IsGreaterThanOrEqualTo(2);
        await Assert.That(events[0].GetProperty("id").GetString()).IsEqualTo(newer.Id);
        await Assert.That(events[0].GetProperty("type").GetString()).IsEqualTo("user.audit_query");
        await Assert.That(events[0].GetProperty("data").GetProperty("n").GetInt32()).IsEqualTo(2);
        await Assert.That(events[0].GetProperty("created_at").GetInt64()).IsGreaterThanOrEqualTo(events[events.GetArrayLength() - 1].GetProperty("created_at").GetInt64());
        await Assert.That(events.EnumerateArray().Any(e => e.GetProperty("id").GetString() == older.Id)).IsTrue();
    }

    [Test]
    public async Task WebhookEndpointManagementLifecycle()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (_, slug, apiKey) = await NewInstanceWithKeyAsync();

        using var noAuth = PostJson($"/v1/{slug}/webhooks", apiKey: null, new { url = "https://ok.example/hook", events = new[] { "user.*" } });
        await Assert.That((await service.Http.SendAsync(noAuth)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var httpUrl = PostJson($"/v1/{slug}/webhooks", apiKey, new { url = "http://insecure.example/hook", events = new[] { "user.*" } });
        await Assert.That((await service.Http.SendAsync(httpUrl)).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var noEvents = PostJson($"/v1/{slug}/webhooks", apiKey, new { url = "https://ok.example/hook", events = Array.Empty<string>() });
        await Assert.That((await service.Http.SendAsync(noEvents)).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        using var created = PostJson($"/v1/{slug}/webhooks", apiKey, new { url = "https://ok.example/hook", events = new[] { "user.created", "org.*" } });
        var createdResponse = await service.Http.SendAsync(created);
        await Assert.That(createdResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var createdBody = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = createdBody.GetProperty("id").GetString()!;
        await Assert.That(createdBody.GetProperty("signing_secret").GetString()!.Length).IsGreaterThanOrEqualTo(32);

        using var list = Get($"/v1/{slug}/webhooks", apiKey);
        var listResponse = await service.Http.SendAsync(list);
        var endpoints = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(endpoints.GetArrayLength()).IsEqualTo(1);
        await Assert.That(endpoints[0].GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(endpoints[0].TryGetProperty("signing_secret", out _)).IsFalse();

        using var deleted = new HttpRequestMessage(HttpMethod.Delete, $"/v1/{slug}/webhooks/{id}");
        deleted.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        await Assert.That((await service.Http.SendAsync(deleted)).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var deletedAgain = new HttpRequestMessage(HttpMethod.Delete, $"/v1/{slug}/webhooks/{id}");
        deletedAgain.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        await Assert.That((await service.Http.SendAsync(deletedAgain)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        using var listAfter = Get($"/v1/{slug}/webhooks", apiKey);
        var after = await (await service.Http.SendAsync(listAfter)).Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(after.GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task RestartedDispatcherRediscoveredInstancesAndDelivers()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, _) = await service.CreateInstanceAsync("hooks-restart-" + Guid.NewGuid().ToString("N")[..8]);
        await using var sink = WebhookSink.Start("redis");

        // Written straight to the store (not via the API): the running app's dispatcher
        // must never learn about this instance — only the restarted one below may deliver.
        var secret = "whsec_" + TerraScale.Auth.Crypto.SecureTokens.RandomToken(24);
        await service.Store.CreateWebhookEndpointAsync(new WebhookEndpoint
        {
            Id = "wh_" + TerraScale.Auth.Crypto.SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Url = sink.BaseUrl + "/redis",
            Events = ["user.*"],
            SigningSecret = secret,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        // A fresh dispatcher simulates a process restart: in-memory cursors start empty,
        // so it must re-discover instances that already hold webhook endpoints.
        var restarted = new WebhookDispatcher(service.Store, NullLogger<WebhookDispatcher>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = restarted.RunAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(4)); // let the discovery tick land before raising the event

        await service.Store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + Guid.NewGuid().ToString("N")[..12],
            InstanceId = instanceId,
            Type = "user.restarted",
            Data = new Dictionary<string, object> { ["via"] = "restart" },
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        var delivery = await WaitForDeliveryAsync(sink, d => d.Headers.GetValueOrDefault("X-TsAuth-Event") == "user.restarted");
        var timestamp = delivery.Headers.GetValueOrDefault("X-TsAuth-Timestamp", "");
        await Assert.That(delivery.Headers.GetValueOrDefault("X-TsAuth-Signature"))
            .IsEqualTo("sha256=" + HexHmac(secret, timestamp, delivery.Body));

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task TimedOutDeliveryIsRetriedNotDropped()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        // First request stalls past the dispatcher's 10s HttpClient timeout; the second must succeed.
        await using var sink = WebhookSink.Start(TimeSpan.FromSeconds(11), "slow");

        using var created = PostJson($"/v1/{slug}/webhooks", apiKey, new { url = sink.BaseUrl + "/slow", events = new[] { "user.slow" } });
        await Assert.That((await service.Http.SendAsync(created)).StatusCode).IsEqualTo(HttpStatusCode.OK);

        await service.Store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + Guid.NewGuid().ToString("N")[..12],
            InstanceId = instanceId,
            Type = "user.slow",
            Data = new Dictionary<string, object>(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        // A timeout must not abort the dispatch tick: the retried attempt hits the sink again.
        var deadline = DateTime.UtcNow.AddSeconds(25);
        var hits = 0;
        while (DateTime.UtcNow < deadline)
        {
            lock (sink.Deliveries)
                hits = sink.Deliveries.Count(d => d.Path.StartsWith("/slow", StringComparison.Ordinal));
            if (hits >= 2)
                break;
            await Task.Delay(250);
        }
        await Assert.That(hits).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task EventMatchingSupportsExactWildcardAndPrefixForms()
    {
        static bool Matches(string[] events, string type) => WebhookDispatcher.Matches(events, type);
        await Assert.That(Matches(["user.created"], "user.created")).IsTrue();         // exact
        await Assert.That(Matches(["user.created"], "user.deleted")).IsFalse();        // exact miss
        await Assert.That(Matches(["*"], "anything.at.all")).IsTrue();                 // wildcard
        await Assert.That(Matches(["user.*"], "user.profile_changed")).IsTrue();       // prefix
        await Assert.That(Matches(["user.*"], "user")).IsFalse();                      // prefix needs the dot
        await Assert.That(Matches(["user.*"], "organization.user.created")).IsFalse(); // no mid-string prefix hits
        await Assert.That(Matches(["user.*", "org.*"], "org.member_added")).IsTrue();  // list of forms
        await Assert.That(Matches(["USER.*"], "user.created")).IsFalse();              // case-sensitive
        await Assert.That(Matches(["user.created"], "USER.CREATED")).IsFalse();        // case-sensitive exact
    }

    [Test]
    public async Task SignatureSchemeIsSha256HexHmacOfTimestampDotBody()
    {
        var signature = WebhookDispatcher.Sign("secret", "1700000000", "{\"a\":1}");
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes("1700000000.{\"a\":1}"))).ToLowerInvariant();
        await Assert.That(signature).IsEqualTo(expected);
        await Assert.That(signature.Length).IsEqualTo("sha256=".Length + 64);
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>Creates an instance and an sk_ key that the InstanceResolver can actually
    /// authenticate: the operator endpoint stores the hash of the raw secret tail while the
    /// resolver hashes the whole presented token, so operator-issued keys cannot resolve yet.
    /// Arranging the key through the store keeps these tests independent of that mismatch.</summary>
    private static async Task<(string InstanceId, string Slug, string ApiKey)> NewInstanceWithKeyAsync()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, _) = await service.CreateInstanceAsync("hooks-" + Guid.NewGuid().ToString("N")[..8]);
        var apiKey = "sk_" + TerraScale.Auth.Crypto.SecureTokens.RandomToken(40);
        await service.Store.CreateApiKeyAsync(new ApiKey
        {
            Id = "key_" + TerraScale.Auth.Crypto.SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Name = "webhooks-e2e",
            SecretKey = TerraScale.Auth.Crypto.SecureTokens.Sha256Hex(apiKey),
            Scopes = ["*"],
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        return (instanceId, slug, apiKey);
    }

    private static HttpRequestMessage Get(string url, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static HttpRequestMessage PostJson(string url, string? apiKey, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (apiKey is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    /// <summary>Raw hex HMAC — compare against X-TsAuth-Signature with its "sha256=" prefix.</summary>
    private static string HexHmac(string secret, string timestamp, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body))).ToLowerInvariant();

    private static async Task<WebhookSink.Delivery> WaitForDeliveryAsync(WebhookSink sink, Func<WebhookSink.Delivery, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            WebhookSink.Delivery? hit;
            lock (sink.Deliveries)
                hit = sink.Deliveries.FirstOrDefault(predicate);
            if (hit is not null)
                return hit;
            await Task.Delay(200);
        }
        lock (sink.Deliveries)
            throw new TimeoutException($"no matching webhook delivery within 15s; got {sink.Deliveries.Count} deliveries on [{string.Join(", ", sink.Deliveries.Select(d => d.Path))}]");
    }

    /// <summary>Local HTTP sink recording every delivery (path, headers, body).
    /// HttpListener cannot bind :0, so it takes the first free port in 18081-18089.
    /// <paramref name="firstHitDelay"/> stalls the very first delivery before
    /// responding (for exercising client-side timeouts).</summary>
    private sealed class WebhookSink(HttpListener listener, string baseUrl, TimeSpan firstHitDelay) : IAsyncDisposable
    {
        public sealed record Delivery(string Path, Dictionary<string, string> Headers, string Body);

        public string BaseUrl => baseUrl;
        public List<Delivery> Deliveries { get; } = [];

        private Task acceptLoop = Task.CompletedTask;

        public static WebhookSink Start(params string[] paths) => Start(TimeSpan.Zero, paths);

        public static WebhookSink Start(TimeSpan firstHitDelay, params string[] paths)
        {
            for (var port = 18081; port <= 18089; port++)
            {
                var candidate = new HttpListener();
                foreach (var path in paths)
                    candidate.Prefixes.Add($"http://127.0.0.1:{port}/{path.Trim('/')}/");
                try
                {
                    candidate.Start();
                    var sink = new WebhookSink(candidate, $"http://127.0.0.1:{port}", firstHitDelay);
                    sink.acceptLoop = Task.Run(sink.AcceptLoopAsync);
                    return sink;
                }
                catch (HttpListenerException)
                {
                    candidate.Close();
                }
            }
            throw new InvalidOperationException("no free webhook sink port in 18081-18089");
        }

        private async Task AcceptLoopAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return; // listener stopped at dispose
                }

                try
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    lock (Deliveries)
                    {
                        Deliveries.Add(new Delivery(
                            context.Request.Url?.AbsolutePath ?? "/",
                            context.Request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => context.Request.Headers[k!] ?? ""),
                            body));
                        if (firstHitDelay > TimeSpan.Zero && Deliveries.Count == 1)
                        {
                            // Hold the first request open past the client timeout, then 200 it.
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(firstHitDelay);
                                try { context.Response.StatusCode = 200; context.Response.Close(); }
                                catch { context.Response.Abort(); }
                            });
                            continue;
                        }
                    }
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                }
                catch (Exception)
                {
                    context.Response.Abort();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            listener.Close();
            await acceptLoop;
        }
    }
}
