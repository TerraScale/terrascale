using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleRegionsClientTests
{
    [Test]
    public async Task GetAsync_SendsAnonymousCanonicalPublicRouteAndDeserializesEveryDetail()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, RegionJson());
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var region = await client.Regions.GetAsync(TerraScaleRegions.Gru);

        await Assert.That(handler.Request).IsEqualTo(new RecordedRequest(
            HttpMethod.Get,
            "https://api.example.invalid/api/v1/regions/GRU",
            Accept: "application/json",
            Authorization: null,
            ApiKey: null));
        await Assert.That(region.Code).IsEqualTo(TerraScaleRegions.Gru);
        await Assert.That(region.Name).IsEqualTo("Sao Paulo");
        await Assert.That(region.Country).IsEqualTo("BR");
        await Assert.That(region.Latitude).IsEqualTo(-23.5475);
        await Assert.That(region.Longitude).IsEqualTo(-46.6361);
        await Assert.That(region.Available).IsTrue();
    }

    [Test]
    public async Task GetAsync_RejectsNonCanonicalRegionCodesWithoutSendingARequest()
    {
        string?[] rejectedCodes =
        [
            null,
            string.Empty,
            " ",
            "gru",
            "sfo",
            "fra",
            " GRU",
            "GRU ",
            "SJC",
            "GR",
            "GRUU",
            "ĞRU",
            "ＧＲＵ"
        ];

        foreach (var rejectedCode in rejectedCodes)
        {
            using var handler = new RecordingHandler(HttpStatusCode.OK, RegionJson());
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(httpClient);

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                client.Regions.GetAsync(rejectedCode!));

            await Assert.That(exception).IsNotNull();
            await Assert.That(handler.Request).IsNull();
        }
    }

    [Test]
    public async Task GetAsync_PropagatesCallerCancellation()
    {
        using var handler = new CancellationHandler();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);
        using var cancellation = new CancellationTokenSource();

        var request = client.Regions.GetAsync(TerraScaleRegions.Gru, cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => request);

        await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task GetAsync_MapsThePublishedNotFoundResponse()
    {
        const string responseBody = "{\"error\":{\"code\":\"not_found\",\"message\":\"The region 'GRU' was not found.\",\"category\":\"not_found\"}}";
        using var handler = new RecordingHandler(HttpStatusCode.NotFound, responseBody);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Regions.GetAsync(TerraScaleRegions.Gru));

        if (exception is null)
        {
            throw new InvalidOperationException("The missing-region fixture did not produce an API exception.");
        }

        await Assert.That(exception.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(exception.Message).IsEqualTo("The region 'GRU' was not found.");
        await Assert.That(exception.ErrorCode).IsEqualTo("not_found");
        await Assert.That(exception.ResponseBody).IsEqualTo(responseBody);
    }

    [Test]
    public async Task GetAsync_RequiresEveryPublishedResponseField()
    {
        foreach (var requiredField in RegionFields.Select(static field => field.Name))
        {
            using var handler = new RecordingHandler(HttpStatusCode.OK, RegionJson(excludedField: requiredField));
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(httpClient);

            var exception = await Assert.ThrowsAsync<JsonException>(() =>
                client.Regions.GetAsync(TerraScaleRegions.Gru));

            await Assert.That(exception).IsNotNull();
        }
    }

    [Test]
    public async Task GetAsync_RejectsNullForRequiredStringFields()
    {
        foreach (var requiredField in new[] { "code", "name", "country" })
        {
            using var handler = new RecordingHandler(HttpStatusCode.OK, RegionJson(nullField: requiredField));
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(httpClient);

            var exception = await Assert.ThrowsAsync<JsonException>(() =>
                client.Regions.GetAsync(TerraScaleRegions.Gru));

            await Assert.That(exception).IsNotNull();
        }
    }

    [Test]
    public async Task GetAsync_RejectsNonCanonicalRegionCodesInSuccessfulResponses()
    {
        string?[] rejectedCodes =
        [
            null,
            string.Empty,
            "gru",
            " GRU",
            "GRU ",
            "SJC",
            "GR",
            "GRUU",
            "ĞRU"
        ];

        foreach (var rejectedCode in rejectedCodes)
        {
            using var handler = new RecordingHandler(
                HttpStatusCode.OK,
                RegionJson(codeJson: rejectedCode is null ? "null" : JsonSerializer.Serialize(rejectedCode)));
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(httpClient);

            var exception = await Assert.ThrowsAsync<JsonException>(() =>
                client.Regions.GetAsync(TerraScaleRegions.Gru));

            await Assert.That(exception).IsNotNull();
        }
    }

    [Test]
    public async Task ListAsync_RejectsNonCanonicalRegionCodesInSuccessfulResponses()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.OK,
            $"{{\"regions\":[{RegionJson(codeJson: "\"gru\"")}]}}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<JsonException>(() => client.Regions.ListAsync());

        await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task PublicRegionModels_DoNotExposeProviderImplementationFields()
    {
        Type[] publicRegionTypes =
        [
            typeof(TerraScaleRegion),
            typeof(TerraScaleRegionDetails)
        ];

        foreach (var regionType in publicRegionTypes)
        {
            await Assert.That(regionType.GetProperty("OciRegionCode")).IsNull();
            await Assert.That(regionType.GetProperty("ClusterNodeTag")).IsNull();
        }
    }

    private static readonly (string Name, string Json)[] RegionFields =
    [
        ("code", "\"code\":\"GRU\""),
        ("name", "\"name\":\"Sao Paulo\""),
        ("country", "\"country\":\"BR\""),
        ("latitude", "\"latitude\":\"-23.5475\""),
        ("longitude", "\"longitude\":\"-46.6361\""),
        ("available", "\"available\":true")
    ];

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key"
    });

    private static string RegionJson(
        string? excludedField = null,
        string? nullField = null,
        string? codeJson = null) =>
        $"{{{string.Join(',', RegionFields
            .Where(field => field.Name != excludedField)
            .Select(field => field.Name == nullField
                ? $"\"{field.Name}\":null"
                : field.Name == "code" && codeJson is not null
                    ? $"\"code\":{codeJson}"
                    : field.Json))}}}";

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public RecordedRequest? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri
                ?? throw new InvalidOperationException("The request URI fixture is missing.");
            Request = new RecordedRequest(
                request.Method,
                requestUri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped),
                Header(request, "Accept"),
                Header(request, "Authorization"),
                Header(request, "X-TerraScale-Auth-Key"));
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation fixture unexpectedly completed.");
        }
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string? Accept,
        string? Authorization,
        string? ApiKey);
}
