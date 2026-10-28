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
