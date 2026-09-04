using System.Net;
using System.Text;
using TerraScale.DotNet;

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
        using var handler = new RecordingHandler("{\"name\":\"orders-copy\",\"placement\":\"GRU\",\"residency\":\"br-only\",\"deleted\":false,\"created_at\":1,\"parent\":\"orders\"}", HttpStatusCode.Created);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var restored = await client.Backups.RestoreAsync("orders", new TerraScaleRestoreRequest("orders-copy", 1_735_689_600_000));

        await Assert.That(handler.Request).IsEqualTo(new RecordedRequest(
            HttpMethod.Post,
            "https://api.example.invalid/api/v1/databases/orders/backups/restore",
            "{\"name\":\"orders-copy\",\"at\":1735689600000}"));
        await Assert.That(restored.Name).IsEqualTo("orders-copy");
    }

    [Test]
    public async Task RestoreAsync_RejectsMissingNameWithoutSendingARequest()
    {
        using var handler = new RecordingHandler("{}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.Backups.RestoreAsync("orders", new TerraScaleRestoreRequest(" ")));

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
