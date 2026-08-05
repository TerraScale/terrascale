using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleBackupConfirmationRequestTests
{
    [Test]
    public async Task PlanAsync_SendsExpectedRouteAndBody()
    {
        using var handler = new RecordingHandler("""
            {"action":"backup.execute","projectId":"project-a","databaseId":"database-a","databaseName":"orders","requestDigest":"digest","confirmationToken":"token","expiresAt":"2026-08-05T12:00:00+00:00"}
            """);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        await client.Backups.PlanAsync(
            "project-a",
            "database-a",
            new TerraScaleBackupConfirmationPlanRequest("session-a"));

        await Assert.That(handler.Request!.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.Request.Uri).IsEqualTo("https://api.example.invalid/api/v1/projects/project-a/databases/database-a/backups:plan");
        await Assert.That(handler.Request.Body).IsEqualTo("{\"operationSessionId\":\"session-a\"}");
        await Assert.That(handler.Request.IdempotencyKey).IsNull();
    }

    [Test]
    public async Task ExecuteAsync_SendsExpectedRouteBodyCallerIdempotencyKeyAndResponseMetadata()
    {
        using var handler = new RecordingHandler("""
            {"backupId":"backup-a","projectId":"project-a","databaseId":"database-a","databaseName":"orders","policy":"on-demand","retention":"default","status":"queued","destinationType":"object-storage","hasRestorePoint":false,"requestedBy":"owner@example.com","requestedAt":"2026-08-05T12:00:00+00:00","updatedAt":"2026-08-05T12:00:00+00:00","completedAt":null,"errorMessage":null}
            """, HttpStatusCode.Accepted, idempotencyReplayed: true);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, () => "factory-key");

        var execution = await client.Backups.ExecuteAsync(
            "project-a",
            "database-a",
            new TerraScaleBackupConfirmationExecuteRequest("session-a", "confirmation-a"),
            " caller-key ");

        await Assert.That(handler.Request!.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.Request.Uri).IsEqualTo("https://api.example.invalid/api/v1/projects/project-a/databases/database-a/backups:execute");
        await Assert.That(handler.Request.Body).IsEqualTo("{\"operationSessionId\":\"session-a\",\"confirmationToken\":\"confirmation-a\"}");
        await Assert.That(handler.Request.IdempotencyKey).IsEqualTo("caller-key");
        await Assert.That(execution.Job.BackupId).IsEqualTo("backup-a");
        await Assert.That(execution.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(execution.IdempotencyReplayed).IsTrue();
    }

    [Test]
    public async Task BackupsClient_DoesNotExposeLegacyTriggerAsync()
    {
        await Assert.That(typeof(TerraScaleBackupsClient).GetMethod("TriggerAsync")).IsNull();
    }

    [Test]
    public async Task ConfirmationRequests_RejectMissingRequiredArguments()
    {
        using var httpClient = new HttpClient(new RecordingHandler("{}"));
        var client = CreateClient(httpClient);

        var planException = await Assert.ThrowsAsync<ArgumentException>(() => client.Backups.PlanAsync(
            "project-a",
            "database-a",
            new TerraScaleBackupConfirmationPlanRequest(" ")));
        var executeException = await Assert.ThrowsAsync<ArgumentException>(() => client.Backups.ExecuteAsync(
            "project-a",
            "database-a",
            new TerraScaleBackupConfirmationExecuteRequest("session-a", "confirmation-a"),
            " "));

        await Assert.That(planException).IsNotNull();
        await Assert.That(executeException).IsNotNull();
    }

    [Test]
    public async Task ConfirmationRequests_RejectValuesBeyondServerLimits()
    {
        using var httpClient = new HttpClient(new RecordingHandler("{}"));
        var client = CreateClient(httpClient);

        var planException = await Assert.ThrowsAsync<ArgumentException>(() => client.Backups.PlanAsync(
            "project-a",
            "database-a",
            new TerraScaleBackupConfirmationPlanRequest(new string('s', 129))));
        var operationSessionException = await Assert.ThrowsAsync<ArgumentException>(() => client.Backups.ExecuteAsync(
            "project-a",
            "database-a",
            new TerraScaleBackupConfirmationExecuteRequest(new string('s', 129), "confirmation-a"),
            "caller-key"));
        var confirmationTokenException = await Assert.ThrowsAsync<ArgumentException>(() => client.Backups.ExecuteAsync(
            "project-a",
            "database-a",
            new TerraScaleBackupConfirmationExecuteRequest("session-a", new string('t', 8193)),
            "caller-key"));

        await Assert.That(planException).IsNotNull();
        await Assert.That(operationSessionException).IsNotNull();
        await Assert.That(confirmationTokenException).IsNotNull();
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient, Func<string>? idempotencyKeyFactory = null) =>
        new(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            ApiKey = "test-key",
            ProjectId = "project-a",
            IdempotencyKeyFactory = idempotencyKeyFactory
        });

    private sealed class RecordingHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        bool idempotencyReplayed = false) : HttpMessageHandler
    {
        public RecordedRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null);
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };

            if (idempotencyReplayed)
            {
                response.Headers.Add("Idempotency-Replayed", "true");
            }

            return response;
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? Body, string? IdempotencyKey);
}
