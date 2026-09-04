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
        _ = await client.Access.ListTeamMembersAsync("project-a");
        _ = await client.Regions.ListAsync();
        _ = await client.Regions.GetAsync(TerraScaleRegions.Gru);

        await Assert.That(handler.Requests).Count().IsEqualTo(4);
        await Assert.That(handler.Requests.All(static request =>
                request.Accept.SequenceEqual(["application/json"], StringComparer.OrdinalIgnoreCase)))
            .IsTrue();
    }


    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key",
        ProjectId = "project-a"
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
                "/api/v1/databases" => "[]",
                "/api/v1/projects/project-a/access/team-members" =>
                    "{\"items\":[],\"totalCount\":0,\"nextCursor\":null}",
                "/api/v1/regions" => "{\"regions\":[]}",
                "/api/v1/regions/GRU" =>
                    "{\"code\":\"GRU\",\"name\":\"Sao Paulo\",\"country\":\"BR\",\"latitude\":\"-23.5475\",\"longitude\":\"-46.6361\",\"available\":true}",
                "/api/v1/repositories/repository-a/events" =>
                    "event: document\ndata: {\"repositoryId\":\"repository-a\",\"repository\":\"orders\",\"collection\":\"orders\",\"id\":\"document-a\",\"type\":\"put\",\"changeToken\":null,\"observedAt\":\"2026-08-30T00:00:00Z\"}\n\n",
                _ => throw new InvalidOperationException($"Unexpected media-type fixture route '{requestUri.AbsolutePath}'.")
            };
            var mediaType = requestUri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal)
                ? "text/event-stream"
                : "application/json";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, mediaType)
            });
        }
    }

    private sealed record RecordedRequest(string Path, IReadOnlyList<string> Accept);
}
