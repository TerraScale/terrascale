using System.Net;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleDatabasesClientTests
{
    [Test]
    public async Task CreateAsync_AllowsGloballyReplicatedPlacementWithinExplicitLock()
    {
        using var handler = new RecordingHandler(HttpStatusCode.BadRequest, "{}");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Databases.CreateAsync(
            "ts-auth", ["GRU", "SFO", "FRA"], "auth-create-1", "GRU, SFO, FRA"));

        await Assert.That(handler.Request!.Uri).IsEqualTo("https://api.example.invalid/api/v1/databases");
        using var body = JsonDocument.Parse(handler.Body!);
        await Assert.That(body.RootElement.GetProperty("residency").GetString()).IsEqualTo("GRU,SFO,FRA");
        await Assert.That(body.RootElement.GetProperty("placement").EnumerateArray().Select(value => value.GetString()!))
            .IsEquivalentTo(["GRU", "SFO", "FRA"]);
    }

    [Test]
    public async Task CreateAsync_RejectsPlacementOutsideLockOrDuplicateRegions()
    {
        using var handler = new RecordingHandler(HttpStatusCode.BadRequest, "{}");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        foreach (var (placement, lockValue) in new (string[], string)[]
        {
            (["GRU", "SFO"], "GRU"),
            (["GRU", "GRU"], "GRU,SFO"),
            (["GRU"], "GRU,GRU"),
            (["GRU"], "GRU,UNKNOWN"),
        })
        {
            Assert.Throws<ArgumentException>(() => client.Databases.CreateAsync("ts-auth", placement, "auth-create-1", lockValue));
        }
        await Assert.That(handler.Request).IsNull();
    }

    [Test]
    public async Task DeleteAsync_SendsConfirmationAndIdempotencyAndReadsAcceptedOperation()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.Accepted,
            DeletionBody("orders", "delete-1"),
            "/api/v1/databases/orders/deletions/delete-1");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        var operation = await client.Databases.DeleteAsync(
            " orders ",
            new TerraScaleDeleteDatabaseOptions("orders", " delete-1 "));

        await Assert.That(operation.Request.DatabaseId).IsEqualTo("orders");
        await Assert.That(operation.Request.OperationId).IsEqualTo("delete-1");
        await Assert.That(operation.State).IsEqualTo("cleanup_pending");
        await Assert.That(handler.Request).IsNotNull();
        await Assert.That(handler.Request!.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(handler.Request.Uri).IsEqualTo("https://api.example.invalid/api/v1/databases/orders");
        await Assert.That(handler.Request.IdempotencyKey).IsEqualTo("delete-1");
        await Assert.That(handler.Request.DeleteConfirmation).IsEqualTo("orders");
    }

    [Test]
    public async Task DeleteAsync_RejectsMissingOrMalformedLocationAndMismatchedOperationIdentity()
    {
        foreach (var variant in new[] { "missing-location", "wrong-operation" })
        {
            using var handler = variant == "missing-location"
                ? new RecordingHandler(HttpStatusCode.Accepted, DeletionBody("orders", "delete-1"))
                : new RecordingHandler(
                    HttpStatusCode.Accepted,
                    DeletionBody("orders", "delete-other"),
                    "/api/v1/databases/orders/deletions/delete-1");
            using var http = new HttpClient(handler);
            var client = CreateClient(http);

            await Assert.ThrowsAsync<JsonException>(() => client.Databases.DeleteAsync(
                "orders",
                new TerraScaleDeleteDatabaseOptions("orders", "delete-1")));
        }
    }

    [Test]
    public async Task DeleteAsync_RejectsUnsupportedSuccessStatusBeforeReadingBody()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "not-json");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Databases.DeleteAsync(
            "orders",
            new TerraScaleDeleteDatabaseOptions("orders", "delete-1")));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(exception.Message).Contains("HTTP 202");
    }

    [Test]
    public async Task DeleteAsync_RejectsMalformedAcceptedBody()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.Accepted,
            "not-json",
            "/api/v1/databases/orders/deletions/delete-1");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        await Assert.ThrowsAsync<JsonException>(() => client.Databases.DeleteAsync(
            "orders",
            new TerraScaleDeleteDatabaseOptions("orders", "delete-1")));
    }

    [Test]
    public async Task GetDeletionStatusAsync_UsesStatusRouteAndRequiresHttp200()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.OK,
            DeletionBody("orders", "delete-1"));
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        var operation = await client.Databases.GetDeletionStatusAsync("orders", "delete-1");

        await Assert.That(operation.Request.OperationId).IsEqualTo("delete-1");
        await Assert.That(handler.Request).IsNotNull();
        await Assert.That(handler.Request!.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(handler.Request.Uri).IsEqualTo("https://api.example.invalid/api/v1/databases/orders/deletions/delete-1");
        await Assert.That(handler.Request.IdempotencyKey).IsNull();
    }

    [Test]
    public async Task GetDeletionStatusAsync_RejectsUnsupportedSuccessStatus()
    {
        using var handler = new RecordingHandler(HttpStatusCode.Accepted, "not-json");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() => client.Databases.GetDeletionStatusAsync(
            "orders",
            "delete-1"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(exception.Message).Contains("HTTP 200");
    }

    [Test]
    public async Task GetDeletionStatusAsync_PropagatesCancellationBeforeSending()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, DeletionBody("orders", "delete-1"));
        using var http = new HttpClient(handler);
        var client = CreateClient(http);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.Databases.GetDeletionStatusAsync(
            "orders",
            "delete-1",
            cancellation.Token));
        await Assert.That(handler.Request).IsNull();
    }

    private static TerraScaleClient CreateClient(HttpClient http) => new(http, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key"
    });

    private static string DeletionBody(string database, string operationId) => $$"""
        {
          "request": {
            "database_id": "{{database}}",
            "environment": "production",
            "operation_id": "{{operationId}}",
            "project_id": "project-1",
            "tenant_id": "tenant-1"
          },
          "requested_at": "2026-09-07T12:00:00Z",
          "state": "cleanup_pending",
          "cutoff": "2026-09-07T12:00:01Z",
          "targets": [
            {
              "stop": {
                "cutoff": "2026-09-07T12:00:01Z",
                "database_id": "{{database}}",
                "environment": "production",
                "operation_id": "{{operationId}}",
                "organization_id": "tenant-1",
                "project_id": "project-1",
                "region": "GRU"
              }
            }
          ]
        }
        """;

    private sealed class RecordingHandler(HttpStatusCode statusCode, string body, string? location = null) : HttpMessageHandler
    {
        public RecordedRequest? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = new RecordedRequest(
                request.Method,
                request.RequestUri?.ToString() ?? throw new InvalidOperationException(),
                request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
                request.Headers.TryGetValues("X-TerraScale-Delete-Confirmation", out var confirmations) ? confirmations.Single() : null);
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (location is not null)
            {
                response.Headers.TryAddWithoutValidation("Location", location);
            }

            return response;
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? IdempotencyKey, string? DeleteConfirmation);
}
