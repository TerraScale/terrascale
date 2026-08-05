using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleDatabaseBranchesClientTests
{
    [Test]
    public async Task GetCapabilityAsync_SendsPublicRouteAndDeserializesCapability()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, """
            {"projectId":"project-a","admissionState":"unavailable","physicalCloneAvailable":false,"promotionAvailable":false,"mergeAvailable":false,"detail":"Physical branches are unavailable."}
            """);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var capability = await client.DatabaseBranches.GetCapabilityAsync("project-a");

        await Assert.That(handler.Request).IsEqualTo(new RecordedRequest(
            HttpMethod.Get,
            "https://api.example.invalid/api/v1/projects/project-a/database-branches/capability",
            null));
        await Assert.That(capability.AdmissionState).IsEqualTo("unavailable");
        await Assert.That(capability.PhysicalCloneAvailable).IsFalse();
    }

    [Test]
    public async Task RequestCloneAsync_SendsPublicRequestAndMapsUnavailableFailure()
    {
        using var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable, """
            {"error":{"code":"TERRASCALE-5001","message":"Service temporarily unavailable. Please retry."}}
            """);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.DatabaseBranches.RequestCloneAsync(
            "project-a",
            new TerraScaleCreateDatabaseBranchRequest("database-a")));

        await Assert.That(handler.Request).IsEqualTo(new RecordedRequest(
            HttpMethod.Post,
            "https://api.example.invalid/api/v1/projects/project-a/database-branches",
            "{\"sourceDatabaseId\":\"database-a\"}"));
        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(exception.ErrorCode).IsEqualTo("TERRASCALE-5001");
    }

    [Test]
    public async Task DatabaseBranchRequests_RejectMissingRequiredArguments()
    {
        using var httpClient = new HttpClient(new RecordingHandler(HttpStatusCode.OK, "{}"));
        var client = CreateClient(httpClient);

        var capabilityException = await Assert.ThrowsAsync<ArgumentException>(() => client.DatabaseBranches.GetCapabilityAsync(" "));
        var cloneException = await Assert.ThrowsAsync<ArgumentException>(() => client.DatabaseBranches.RequestCloneAsync(
            "project-a",
            new TerraScaleCreateDatabaseBranchRequest(" ")));

        await Assert.That(capabilityException).IsNotNull();
        await Assert.That(cloneException).IsNotNull();
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key",
        ProjectId = "project-a"
    });

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public RecordedRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? Body);
}
