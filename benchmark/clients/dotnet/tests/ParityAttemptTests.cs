using System.Net;
using ApiBench;
using TerraScale.DotNet.TerraBase;
using TUnit.Core;

namespace BenchClient.Tests;

public sealed class ParityAttemptTests
{
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Id = "01ARZ3NDEKTSV4RRFFQ69G5FAA";
    private static readonly Uri Origin = new("https://api.terrascale.tech/");
    private static Uri Target => Http.ReadUri(Origin, Project, "bench-x", "seed", Id);

    [Test]
    [Arguments("ok", null)]
    [Arguments("h1", "protocol")]
    [Arguments("wrong-region", "region")]
    [Arguments("duplicate-region", "region")]
    [Arguments("missing-region", "region")]
    [Arguments("unpin", "pin")]
    [Arguments("auth", "target")]
    [Arguments("401", "status")]
    public async Task MetadataGateRejectsEveryInvalidAttemptWithoutChangingTheResponse(string variant, string? expected)
    {
        using var observer = new ParityAttempts(new FrameHandler(variant), Target, "gru", HttpVersion.Version20);
        using var client = new HttpClient(observer);
        using var request = new HttpRequestMessage(HttpMethod.Get, variant == "auth" ? new Uri(Origin, "/oidc/token") : Target);
        if (variant != "unpin") request.Headers.Add("X-TerraScale-Region-Pin", "gru");
        using var response = await client.SendAsync(request);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(FrameHandler.Body(variant == "401"));
        await Assert.That(observer.Snapshot.RejectionSince(default, 1)).IsEqualTo(expected);
        if (variant == "unpin") await Assert.That(request.Headers.Contains("X-TerraScale-Region-Pin")).IsFalse();
    }

    [Test]
    public async Task ShippedSdkRetryIsPreservedAndRejectsTheCell()
    {
        using var observer = new ParityAttempts(new FrameHandler("retry"), Target, "gru", requiredProtocol: null);
        using var http = new HttpClient(observer);
        var documents = Documents(http);
        var document = await documents.Database(Project, "bench-x").GetDocumentAsync("seed", Id);
        await Assert.That(document?.Id).IsEqualTo(Id);
        await Assert.That(observer.Snapshot.Attempts).IsEqualTo(2);
        await Assert.That(observer.Snapshot.Ok).IsEqualTo(1);
        await Assert.That(observer.Snapshot.RejectionSince(default, 1)).IsEqualTo("attempts");
    }

    [Test]
    public async Task ShippedSdkDoesNotRefreshOrRetryA401()
    {
        using var observer = new ParityAttempts(new FrameHandler("401"), Target, "gru", requiredProtocol: null);
        using var http = new HttpClient(observer);
        TerraBaseAuthenticationException? refused = null;
        try
        {
            await Documents(http).Database(Project, "bench-x").GetDocumentAsync("seed", Id);
        }
        catch (TerraBaseAuthenticationException error)
        {
            refused = error;
        }
        await Assert.That(refused).IsNotNull();
        await Assert.That(observer.Snapshot.Attempts).IsEqualTo(1);
        await Assert.That(observer.Snapshot.RejectionSince(default, 1)).IsEqualTo("status");
    }

    [Test]
    public async Task DrainedSnapshotsExcludeReadinessAndKeepProtocolCounts()
    {
        using var observer = new ParityAttempts(new FrameHandler("ok"), Target, "gru", requiredProtocol: null);
        using var client = new HttpClient(observer);
        async Task SendAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Target);
            request.Headers.Add("X-TerraScale-Region-Pin", "gru");
            using var response = await client.SendAsync(request);
        }
        await SendAsync();
        var before = observer.Snapshot;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => SendAsync()));
        var after = observer.Snapshot;
        await Assert.That(after.RejectionSince(before, 16)).IsNull();
        await Assert.That(after.H2 - before.H2).IsEqualTo(16);
        await Assert.That(after.H1 - before.H1).IsEqualTo(0);
        await Assert.That(after.RejectionSince(after, 0)).IsEqualTo("empty");
    }

    private static TerraBaseDocumentsClient Documents(HttpClient http) => new(http,
        new TerraBaseDocumentsClientOptions { BaseAddress = Origin, Region = "gru", AccessTokenProvider = _ => ValueTask.FromResult("synthetic-control") });

    private sealed class FrameHandler(string variant) : HttpMessageHandler
    {
        private int calls;
        internal static string Body(bool refused) => refused
            ? """{"error":{"code":"invalid_token","message":"control"}}"""
            : """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAA","version":"v1","state":"value","document":{"g":1}}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var retry = variant == "retry" && Interlocked.Increment(ref calls) == 1;
            var response = new HttpResponseMessage(retry ? HttpStatusCode.ServiceUnavailable : variant == "401" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Version = variant == "h1" ? HttpVersion.Version11 : HttpVersion.Version20,
                Content = new StringContent(retry ? """{"error":{"code":"catalog_stale","message":"control"}}""" : Body(variant == "401")),
            };
            if (retry) response.Headers.RetryAfter = new(TimeSpan.Zero);
            if (variant != "missing-region") response.Headers.TryAddWithoutValidation("X-TerraBase-Region", variant == "wrong-region" ? "sjc" : "gru");
            if (variant == "duplicate-region") response.Headers.TryAddWithoutValidation("X-TerraBase-Region", "gru");
            return Task.FromResult(response);
        }
    }
}
