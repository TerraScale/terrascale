using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ApiBench;
using TerraScale.DotNet;

namespace BenchClient.Tests;

/// <summary>Synthetic auth: a token response whose lifetime decides whether every read refreshes it.</summary>
internal sealed class FakeAuth(long expiresIn) : HttpMessageHandler
{
    internal const string Key = "01ARZ3NDEKTSV4RRFFQ69G5FAV_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"access_token":"synthetic-control","token_type":"Bearer","expires_in":{{expiresIn}}}""", Encoding.UTF8, "application/json"),
        });

    internal static (Tokens Tokens, ParityAuth Observer, HttpClient Http) Create(long expiresIn)
    {
        var observer = new ParityAuth(new FakeAuth(expiresIn));
        var http = new HttpClient(observer);
        var auth = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions { BaseAddress = new Uri("https://auth.terrascale.tech"), InstanceSlug = "terrascale" });
        return (new Tokens(auth.Oidc, Key), observer, http);
    }
}

/// <summary>Synthetic TerraBase reads. Call numbers start at 1; the first read is readiness.</summary>
internal class FakeReads(string variant, string region = "gru") : HttpMessageHandler
{
    internal const string Id = "01ARZ3NDEKTSV4RRFFQ69G5FAA";
    internal static readonly string Body = Payload.Json(0, 256);
    private int reads;

    internal ConcurrentDictionary<(Version, HttpVersionPolicy), bool> Policies { get; } = new();

    internal static ParityDocument Document => new(Id, "v1", Body);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Route(request));

    protected virtual HttpResponseMessage Route(HttpRequestMessage request)
    {
        Policies[(request.Version, request.VersionPolicy)] = true;
        var call = Interlocked.Increment(ref reads);
        var (status, code) = (variant, call) switch
        {
            ("missing", <= 2) or ("absent", _) => (HttpStatusCode.NotFound, "not_found"),
            ("retry", 2) => (HttpStatusCode.ServiceUnavailable, "catalog_stale"),
            ("500", 2) => (HttpStatusCode.InternalServerError, "internal"),
            ("401", 2) => (HttpStatusCode.Unauthorized, "invalid_token"),
            _ => (HttpStatusCode.OK, null),
        };
        var body = code is null
            ? $$"""{"id":"{{Id}}","version":"v1","state":"value","document":{{(variant == "document" && call >= 2 ? Body.Replace('x', 'y') : Body)}}}"""
            : $$$"""{"error":{"code":"{{{code}}}","message":"control"}}""";
        var response = Reply(status, body);
        response.Version = variant == "h1" ? HttpVersion.Version11 : request.Version;
        if (status == HttpStatusCode.ServiceUnavailable) response.Headers.RetryAfter = new(TimeSpan.Zero);
        response.Headers.TryAddWithoutValidation("X-TerraBase-Region", variant == "region" ? "sjc" : region);
        return response;
    }

    internal static HttpResponseMessage Reply(HttpStatusCode status, string? body) => new(status)
    {
        Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

/// <summary>Synthetic TerraBase for whole runs: setup, exact seeded reads and owned-database deletion.</summary>
internal sealed class FakeTerraBase(params string[] modes) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, string> documents = new();
    private int requests;

    internal int Requests => Volatile.Read(ref requests);

    internal ConcurrentQueue<string> Setup { get; } = new();

    internal Func<string>? OutputAtCreate { get; set; }

    internal string? Owned { get; private set; }

    internal TaskCompletionSource CreateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource DeleteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource DeleteRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool DeleteCanceled { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requests);
        var path = request.RequestUri!.AbsolutePath;
        var id = request.RequestUri.Query.StartsWith("?id=", StringComparison.Ordinal) ? Uri.UnescapeDataString(request.RequestUri.Query[4..]) : "";
        var pin = request.Headers.TryGetValues("X-TerraScale-Region-Pin", out var pins) ? string.Join(',', pins) : "none";
        if (request.Method == HttpMethod.Get && path.EndsWith("/documents/by-key", StringComparison.Ordinal))
        {
            var response = FakeReads.Reply(HttpStatusCode.OK, $$"""{"id":"{{id}}","version":"v-{{id}}","state":"value","document":{{documents[id]}}}""");
            response.Version = request.Version;
            response.Headers.TryAddWithoutValidation("X-TerraBase-Region", modes.Contains("sjc-region") && pin == "sjc" ? "gru" : pin);
            return response;
        }
        Setup.Enqueue($"{request.Method} {pin}");
        if (request.Method == HttpMethod.Post && path.EndsWith("/databases", StringComparison.Ordinal))
        {
            Owned = OutputAtCreate?.Invoke();
            CreateEntered.TrySetResult();
            if (modes.Contains("create-block")) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (modes.Contains("create-transport")) throw new HttpRequestException("synthetic lost connection");
            if (modes.Contains("create-refused")) return FakeReads.Reply(HttpStatusCode.Forbidden, """{"error":{"code":"forbidden","message":"control"}}""");
            return FakeReads.Reply(HttpStatusCode.Created, """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAB","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","name":"x","branchedFrom":null,"createdAt":"2026-10-28T00:00:00+00:00","region":"fra","homeRegion":"fra","replicaRegions":[],"allRegions":true}""");
        }
        if (request.Method == HttpMethod.Post)
        {
            if (modes.Contains("collection-hang")) await Task.Delay(Timeout.Infinite, cancellationToken);
            return FakeReads.Reply(HttpStatusCode.Created, """{"name":"bench","created_at":"0","id":"01ARZ3NDEKTSV4RRFFQ69G5FAC"}""");
        }
        if (request.Method == HttpMethod.Put)
        {
            documents[id] = await request.Content!.ReadAsStringAsync(cancellationToken);
            return FakeReads.Reply(HttpStatusCode.Created, $$"""{"id":"{{id}}","version":"v-{{id}}","offset":1,"durability":"replicated"}""");
        }
        DeleteEntered.TrySetResult();
        if (modes.Contains("delete-block")) await DeleteRelease.Task;
        DeleteCanceled = cancellationToken.IsCancellationRequested;
        return modes.Contains("delete-404") ? FakeReads.Reply(HttpStatusCode.NotFound, """{"error":{"code":"not_found","message":"control"}}""")
            : modes.Contains("delete-500") ? FakeReads.Reply(HttpStatusCode.InternalServerError, """{"error":{"code":"internal","message":"control"}}""")
            : FakeReads.Reply(HttpStatusCode.NoContent, null);
    }

    // Shared by every lane of a run; lanes dispose their clients, which must not end this fake.
    protected override void Dispose(bool disposing)
    {
    }

    internal ParityRig Rig(TimeSpan? setup = null) => new(_ => this, () => new FakeAuth(3600),
        concurrency => new ParityCellPlan(new ParityWindowPlan(concurrency, TimeSpan.Zero, TimeSpan.FromMilliseconds(2), TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(10), TimeSpan.Zero),
        setup ?? TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), TimeProvider.System);
}
