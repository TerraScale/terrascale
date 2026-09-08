using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleMediaTypeContractTests
{
    [Test]
    public async Task JsonOperations_SendThePublishedAcceptMediaTypeAcrossRequestPaths()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        _ = await client.Databases.ListAsync();
        _ = await client.Regions.ListAsync();

        await Assert.That(handler.Requests).Count().IsEqualTo(2);
        await Assert.That(handler.Requests.All(static request =>
                request.Accept.SequenceEqual(["application/json"], StringComparer.OrdinalIgnoreCase)))
            .IsTrue();
    }


    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key"
    });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri
                ?? throw new InvalidOperationException("The request URI fixture is missing.");
            Requests.Add(new RecordedRequest(
                requestUri.AbsolutePath,
                request.Headers.Accept
                    .Select(static value => value.MediaType ?? string.Empty)
                    .ToArray()));

            var responseBody = requestUri.AbsolutePath switch
            {
                "/api/v1/databases" => "{\"databases\":[]}",
                "/api/v1/regions" => "{\"regions\":[{\"code\":\"GRU\",\"name\":\"Sao Paulo\",\"country\":\"BR\",\"latitude\":-23.43,\"longitude\":-46.47,\"available\":true}],\"hosts\":{\"GRU\":\"gru.api.terrascale.tech\"}}",
                _ => throw new InvalidOperationException($"Unexpected media-type fixture route '{requestUri.AbsolutePath}'.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record RecordedRequest(string Path, IReadOnlyList<string> Accept);
}
