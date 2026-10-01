using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.DotNet.Native;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Native;

public sealed class NativeDiscoveryRouterTests
{
    private static readonly NativeScope Scope = new(
        "10000000-0000-0000-0000-000000000001",
        "20000000-0000-0000-0000-000000000001", ulong.MaxValue);
    private static readonly string Credential = "tbk1." + Encode(new byte[16]) + "." + Encode(new byte[32]);

    [Test]
    public async Task AuthenticatedNativeScopeSelectsClosestCapableDirectHost()
    {
        var clock = new TestClock();
        var document = Body(clock, Endpoint("GRU", write: false), Endpoint("FRA", priority: 2));
        var transport = new Transport(document);
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3), transport, clock);

        var admission = await router.ResolveAsync(Scope, Credential, ["client_v1", "write"], CancellationToken.None);
        admission.AssertCurrent();

        await Assert.That(admission.Origin.ToString()).IsEqualTo("https://fra.api.terrascale.tech/");
        await Assert.That(transport.Requests.Count).IsEqualTo(1);
        await Assert.That(transport.Requests[0]).IsEqualTo(
            $"https://atlas.terrascale.tech/api/v1/discovery?database_id={Scope.Database}&shard_id=18446744073709551615&tenant_id={Scope.Tenant}");
        await Assert.That(transport.Authorization).IsEqualTo("Bearer " + Credential);
    }

    [Test]
    public async Task ForbiddenDiscoveryPreservesServerCodeAndNeverFallsBack()
    {
        var transport = new Transport("{\"error\":{\"code\":\"discovery_forbidden\"}}", HttpStatusCode.Forbidden);
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3), transport);

        var failure = await CaptureAsync(() => router.ResolveAsync(Scope, Credential, ["read"], CancellationToken.None));

        await Assert.That(failure.Code).IsEqualTo("discovery_forbidden");
        await Assert.That(failure.Status).IsEqualTo((int)HttpStatusCode.Forbidden);
        await Assert.That(transport.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ManagedAtlasRejectsShardZeroBeforeDiscovery()
    {
        var transport = new Transport("{}");
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3), transport);
        var failure = await CaptureAsync(() => router.ResolveAsync(
            new NativeScope(Scope.Tenant, Scope.Database, 0), Credential, ["read"], CancellationToken.None));
        await Assert.That(failure.Code).IsEqualTo("invalid_scope");
        await Assert.That(transport.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task UnknownUpstreamErrorCodeCannotLeakASecret()
    {
        const string canary = "secret-canary-discovery-7e50a9";
        var transport = new Transport(JsonSerializer.Serialize(new { error = new { code = canary } }), HttpStatusCode.Forbidden);
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3), transport);
        var failure = await CaptureAsync(() => router.ResolveAsync(Scope, Credential, ["read"], CancellationToken.None));
        await Assert.That(failure.Code).IsEqualTo("discovery_http_error");
        await Assert.That(failure.Status).IsEqualTo((int)HttpStatusCode.Forbidden);
        await Assert.That(failure.Message).DoesNotContain(canary);
        await Assert.That(failure.ToString()).DoesNotContain(canary);
    }

    [Test]
    public async Task DiscoveryDeadlineStillBoundsBodyAfterResponseHeaders()
    {
        var body = new StallStream();
        var transport = new HeaderOnlyTransport(body);
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(2), transport);
        var failure = await CaptureAsync(() => router.ResolveAsync(Scope, Credential, ["read"], CancellationToken.None));
        await Assert.That(failure.Code).IsEqualTo("discovery_unavailable");
        await Assert.That(failure.Status).IsNull();
        await Assert.That(body.CancellationObserved).IsTrue();
        await Assert.That(transport.Requests).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidCredentialFailsBeforeDiscovery()
    {
        var transport = new Transport("{}");
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3), transport);
        var invalidCredential = await CaptureAsync(() => router.ResolveAsync(Scope, "oidc-token", ["read"], CancellationToken.None));
        await Assert.That(invalidCredential.Code).IsEqualTo("invalid_credential");
        await Assert.That(transport.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DiscoveryScopeMismatchCannotAdmitAnEndpoint()
    {
        var clock = new TestClock();
        var document = JsonNode.Parse(Body(clock, Endpoint("GRU")))!;
        document["scope"]!["tenant_id"] = "10000000-0000-0000-0000-000000000002";
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3), new Transport(document.ToJsonString()), clock);

        var failure = await CaptureAsync(() => router.ResolveAsync(Scope, Credential.Replace("AAAA", "AgAA", StringComparison.Ordinal), ["read"], CancellationToken.None));
        await Assert.That(failure.Code).IsEqualTo("invalid_discovery");
    }

    [Test]
    [Arguments("https://gru.terrascale.tech")]
    [Arguments("https://api.terrascale.tech")]
    [Arguments("http://127.0.0.1:12345")]
    [Arguments("https://gru.api.terrascale.tech:444")]
    [Arguments("https://gru.api.terrascale.tech/path")]
    public async Task ProductionMetadataCannotSelectLegacyOrDevelopmentOrigins(string origin)
    {
        var clock = new TestClock();
        var document = JsonNode.Parse(Body(clock, Endpoint("GRU")))!;
        document["endpoints"]![0]!["url"] = origin;
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3), new Transport(document.ToJsonString()), clock);

        var failure = await CaptureAsync(() => router.ResolveAsync(Scope, Credential.Replace("AAAA", "AgAA", StringComparison.Ordinal), ["read"], CancellationToken.None));
        await Assert.That(failure.Code).IsEqualTo("invalid_discovery");
    }

    [Test]
    [Arguments("gru", "https://gru.api.terrascale.tech")]
    [Arguments("ATLAS", "https://atlas.terrascale.tech")]
    [Arguments("GRU", "https://gru.api.terrascale.tech.attacker.example")]
    [Arguments("BRA", "https://gru.api.terrascale.tech")]
    public async Task DiscoveryRequiresCanonicalNonReservedMatchingRegionalIdentity(string region, string origin)
    {
        var clock = new TestClock();
        var document = JsonNode.Parse(Body(clock, Endpoint("GRU")))!;
        document["endpoints"]![0]!["region"] = region;
        document["endpoints"]![0]!["url"] = origin;
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3), new Transport(document.ToJsonString()), clock);

        var failure = await CaptureAsync(() => router.ResolveAsync(Scope, Credential.Replace("AAAA", "AgAA", StringComparison.Ordinal), ["read"], CancellationToken.None));
        await Assert.That(failure.Code).IsEqualTo("invalid_discovery");
    }

    [Test]
    public async Task ExpiredDiscoveryAndAdmissionCannotBeUsed()
    {
        var clock = new TestClock();
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3), new Transport(Body(clock, Endpoint("GRU"))), clock);
        var admission = await router.ResolveAsync(Scope, Credential, ["read"], CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(8));

        var failure = await CaptureAsync(() =>
        {
            admission.AssertCurrent();
            return ValueTask.FromResult(admission);
        });
        await Assert.That(failure.Code).IsEqualTo("discovery_expired");
        clock.Advance(TimeSpan.FromSeconds(1));
        failure = await CaptureAsync(() => router.ResolveAsync(Scope, Credential, ["read"], CancellationToken.None));
        await Assert.That(failure.Code).IsEqualTo("discovery_expired");
    }

    [Test]
    public async Task RevisionFloorRejectsRegressionAndFencesAnOlderAdmission()
    {
        var clock = new TestClock();
        var transport = new Transport(Body(clock, Endpoint("GRU")));
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3), transport, clock);
        var first = await router.ResolveAsync(Scope, Credential, ["read"], CancellationToken.None);
        var successor = JsonNode.Parse(transport.Body)!;
        successor["revision"] = "42";
        transport.Body = successor.ToJsonString();
        await router.ResolveAsync(Scope, Credential.Replace("AAAA", "AQAA", StringComparison.Ordinal), ["read"], CancellationToken.None);
        var fenced = await CaptureAsync(() => { first.AssertCurrent(); return ValueTask.FromResult(first); });
        await Assert.That(fenced.Code).IsEqualTo("routing_observation_regressed");
        successor["revision"] = "41";
        transport.Body = successor.ToJsonString();
        var regressed = await CaptureAsync(() => router.ResolveAsync(Scope, Credential, ["read"], CancellationToken.None));
        await Assert.That(regressed.Code).IsEqualTo("routing_observation_regressed");
        await Assert.That(transport.Requests[2]).Contains("rev=42");
        await Assert.That(transport.Requests[2]).Contains("observed_publisher_generation=18446744073709551615");
    }

    [Test]
    public async Task PublisherConflictAndDuplicateWireFieldsAreRejected()
    {
        var clock = new TestClock();
        var transport = new Transport(Body(clock, Endpoint("GRU")));
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3), transport, clock);
        await router.ResolveAsync(Scope, Credential, ["read"], CancellationToken.None);
        var conflict = JsonNode.Parse(transport.Body)!;
        conflict["authority"]!["publisher_id"] = "publisher-b";
        transport.Body = conflict.ToJsonString();
        var failure = await CaptureAsync(() => router.ResolveAsync(Scope, Credential.Replace("AAAA", "AQAA", StringComparison.Ordinal), ["read"], CancellationToken.None));
        await Assert.That(failure.Code).IsEqualTo("routing_observation_regressed");
        transport.Body = Body(clock, Endpoint("GRU")).Replace("\"revision\":\"41\"", "\"revision\":\"41\",\"revision\":\"42\"", StringComparison.Ordinal);
        failure = await CaptureAsync(() => router.ResolveAsync(Scope, Credential.Replace("AAAA", "AgAA", StringComparison.Ordinal), ["read"], CancellationToken.None));
        await Assert.That(failure.Code).IsEqualTo("invalid_discovery");
    }

    [Test]
    public async Task UnconsumedAdvancedWriterCannotBeClaimedAsAdmitted()
    {
        var clock = new TestClock();
        var document = JsonNode.Parse(Body(clock, Endpoint("GRU")))!;
        document["endpoints"]![0]!["capabilities"]!["atomic_groups"] = true;
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3), new Transport(document.ToJsonString()), clock);
        var failure = await CaptureAsync(() => router.ResolveAsync(Scope, Credential, ["atomic_groups"], CancellationToken.None));
        await Assert.That(failure.Code).IsEqualTo("unsupported_discovery_writer");
    }

    [Test]
    public async Task ParallelSameCredentialResolvesShareLeaseAndCredentialChangeFencesOldAdmissions()
    {
        var clock = new TestClock();
        var transport = new Transport(Body(clock, Endpoint("GRU")));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        using var http = new HttpClient(new NativeClientTests.Handler(async (_, token) =>
        {
            Interlocked.Increment(ref fetches);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return NativeClientTests.Json(transport.Body);
        }));
        using var router = new NativeDiscoveryRouter(http, TimeSpan.FromSeconds(3), clock: clock);
        var pending = Enumerable.Range(0, 32).Select(_ =>
            router.ResolveAsync(Scope, Credential, ["read"], CancellationToken.None).AsTask()).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();
        var admissions = await Task.WhenAll(pending);
        await Assert.That(fetches).IsEqualTo(1);
        foreach (var admission in admissions) admission.AssertCurrent();
        var successor = JsonNode.Parse(transport.Body)!;
        successor["revision"] = "42";
        transport.Body = successor.ToJsonString();
        var next = await router.ResolveAsync(Scope, Credential.Replace("AAAA", "AQAA", StringComparison.Ordinal), ["read"], CancellationToken.None);
        next.AssertCurrent();
        await Assert.That(fetches).IsEqualTo(2);
        foreach (var admission in admissions)
        {
            var fenced = await CaptureAsync(() => { admission.AssertCurrent(); return ValueTask.FromResult(admission); });
            await Assert.That(fenced.Code).IsEqualTo("routing_observation_regressed");
        }
    }

    private static object Endpoint(string region, bool write = true, int priority = 1) => new
    {
        region,
        url = "https://" + region.ToLowerInvariant() + ".api.terrascale.tech",
        catalog = new { digest = new string('c', 64) },
        capabilities = new { client_v1 = true, read = true, query = true, subscriptions = true, write, conditional_mutation = false, atomic_groups = false, security_state = false },
        security_writer = (object?)null,
        priority
    };

    private static string Body(TestClock clock, params object[] endpoints) => JsonSerializer.Serialize(new
    {
        schema_version = 1,
        scope = new { tenant_id = Scope.Tenant, database_id = Scope.Database, shard_id = Scope.Shard.ToString(CultureInfo.InvariantCulture) },
        authority = new { deployment_id = "fixture-deployment", cell = "gru", publisher_id = "publisher-a", publisher_generation = ulong.MaxValue.ToString(CultureInfo.InvariantCulture) },
        revision = "41",
        published_at = clock.GetUtcNow().ToUnixTimeSeconds(),
        expires_at = clock.GetUtcNow().ToUnixTimeSeconds() + 9,
        refreshSeconds = 8,
        residency = (object?)null,
        endpoints
    });

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<NativeAdmissionException> CaptureAsync(Func<ValueTask<NativeAdmission>> action)
    {
        try { await action(); }
        catch (NativeAdmissionException exception) { return exception; }
        throw new InvalidOperationException("Expected native discovery to fail closed.");
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly DateTimeOffset initial = DateTimeOffset.FromUnixTimeSeconds(1790769600);
        private TimeSpan elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => initial + elapsed;
        internal void Advance(TimeSpan duration) => elapsed += duration;
    }

    private sealed class Transport(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal string Body { get; set; } = body;
        internal List<string> Requests { get; } = [];
        internal string? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.OriginalString);
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class HeaderOnlyTransport(Stream body) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var content = new StreamContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class StallStream : Stream
    {
        internal bool CancellationObserved { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
            return 0;
        }
    }
}
