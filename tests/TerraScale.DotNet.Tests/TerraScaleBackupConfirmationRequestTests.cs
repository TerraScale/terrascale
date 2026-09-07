using System.Net;
using System.Text;
using TerraScale.DotNet;
using TerraScale.DotNet.Generated;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleBackupConfirmationRequestTests
{
    [Test]
    public async Task ListAsync_UsesDirectDatabaseReadinessRouteAndBody()
    {
        using var handler = new RecordingHandler("{\"ready\":true,\"streaming\":false}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var status = await client.Backups.ListAsync("orders");

        await Assert.That(handler.Request).IsEqualTo(new RecordedRequest(
            HttpMethod.Get,
            "https://api.example.invalid/api/v1/databases/orders/backups",
            null));
        await Assert.That(status.Ready).IsTrue();
        await Assert.That(status.Streaming).IsFalse();
    }

    [Test]
    public async Task RestoreAsync_UsesDirectRouteAndRestoreToNewDatabaseBody()
    {
        using var handler = new RecordingHandler("{\"environment\":\"production\",\"tenant_id\":\"tenant-1\",\"project_id\":\"project-1\",\"operation_id\":\"restore-op-1\",\"database_id\":\"db-2\",\"parent_database_id\":\"db-1\",\"key_version\":\"key-1\",\"name\":\"orders-copy\",\"regions\":[\"GRU\"],\"residency\":\"br-only\",\"state\":\"pending\"}", HttpStatusCode.Accepted);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var restored = await client.Backups.RestoreAsync("orders", new RestoreRequest(1_735_689_600_000, "orders-copy", "restore-op-1"));

        await Assert.That(handler.Request).IsEqualTo(new RecordedRequest(
            HttpMethod.Post,
            "https://api.example.invalid/api/v1/databases/orders/backups/restore",
            "{\"at\":1735689600000,\"name\":\"orders-copy\",\"operation_id\":\"restore-op-1\"}"));
        await Assert.That(restored.Name).IsEqualTo("orders-copy");
        await Assert.That(restored.OperationId).IsEqualTo("restore-op-1");
    }

    [Test]
    public async Task RestoreAsync_RejectsMissingNameWithoutSendingARequest()
    {
        using var handler = new RecordingHandler("{}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.Backups.RestoreAsync("orders", new RestoreRequest(null, " ", "restore-op-1")));

        await Assert.That(exception).IsNotNull();
        await Assert.That(handler.Request).IsNull();
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key"
    });

    private sealed class RecordingHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public RecordedRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = new RecordedRequest(request.Method, request.RequestUri?.ToString() ?? throw new InvalidOperationException(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? Body);
}
