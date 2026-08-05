using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleEnvironmentsClientTests
{
    [Test]
    public async Task DeleteAsync_SendsThePublicRouteAndDeserializesDurableDeletionFields()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            ApiKey = "test-key",
            ProjectId = "project-a"
        });

        var deletion = await client.Environments.DeleteAsync("project-a", "database-a", "environment-a");

        await Assert.That(handler.Request).IsEqualTo(new RecordedRequest(
            HttpMethod.Delete,
            "https://api.example.invalid/api/v1/projects/project-a/databases/database-a/environments/environment-a",
            null));
        await Assert.That(deletion.OperationId).IsEqualTo("01J00000000000000000000003");
        await Assert.That(deletion.Status).IsEqualTo("RetryScheduled");
        await Assert.That(deletion.RetryAt).IsEqualTo(DateTimeOffset.Parse("2026-08-05T12:05:00+00:00"));
        await Assert.That(deletion.Deleted).IsFalse();
    }

    [Test]
    public async Task DeletedEnvironmentResponse_LeavesMissingStatusUnset()
    {
        var response = JsonSerializer.Deserialize<TerraScaleDeletedProjectDatabaseEnvironment>("""
            {"environmentId":"environment-a","projectId":"project-a","databaseId":"database-a","name":"Preview","deleted":true,"physicalDatabaseDeleted":false,"repositoriesDeleted":0}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.Status).IsNull();
        await Assert.That(response.OperationId).IsNull();
        await Assert.That(response.RetryAt).IsNull();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public RecordedRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"environmentId":"environment-a","projectId":"project-a","databaseId":"database-a","name":"Preview","deleted":false,"physicalDatabaseDeleted":false,"repositoriesDeleted":0,"operationId":"01J00000000000000000000003","status":"RetryScheduled","retryAt":"2026-08-05T12:05:00+00:00"}
                    """, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? Body);
}
