using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuditClientTests
{
    [Test]
    public async Task AuditOperations_SendCanonicalRoutesAndDeserializeEveryField()
    {
        using var handler = new AuditHandler();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var events = await client.Audit.ListAsync("project-a", skip: 5, take: 25);
        var loaded = await client.Audit.GetAsync("project-a", "audit-a");

        await Assert.That(handler.Paths)
            .IsEquivalentTo(
            [
                "/api/v1/projects/project-a/audit?skip=5&take=25",
                "/api/v1/projects/project-a/audit/audit-a"
            ]);
        await Assert.That(events).Count().IsEqualTo(1);
        await Assert.That(loaded.AuditEventId).IsEqualTo("audit-a");
        await Assert.That(loaded.ProjectId).IsEqualTo("project-a");
        await Assert.That(loaded.Actor).IsEqualTo("user-a");
        await Assert.That(loaded.Action).IsEqualTo("repository.created");
        await Assert.That(loaded.Target).IsEqualTo("repository-a");
        await Assert.That(loaded.Risk).IsEqualTo("normal");
        await Assert.That(loaded.Source).IsEqualTo("dashboard");
        await Assert.That(loaded.OccurredAt)
            .IsEqualTo(DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
    }

    [Test]
    public async Task AuditOperations_RejectInvalidArgumentsBeforeSending()
    {
        using var handler = new AuditHandler();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        _ = await Assert.ThrowsAsync<ArgumentException>(() => client.Audit.ListAsync(" "));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.Audit.ListAsync("project-a", take: 0));
        _ = await Assert.ThrowsAsync<ArgumentException>(() => client.Audit.GetAsync("project-a", " "));

        await Assert.That(handler.Paths).IsEmpty();
    }

    [Test]
    public async Task AuditResponses_RequireEveryPublishedField()
    {
        string[] fields =
        [
            "auditEventId",
            "projectId",
            "actor",
            "action",
            "target",
            "risk",
            "source",
            "occurredAt"
        ];
        foreach (var field in fields)
        {
            using var handler = new AuditHandler(field);
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(httpClient);

            var exception = await Assert.ThrowsAsync<JsonException>(() =>
                client.Audit.GetAsync("project-a", "audit-a"));

            await Assert.That(exception).IsNotNull();
        }
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(
        httpClient,
        new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            BearerToken = "caller-token"
        });

    private sealed class AuditHandler(string? excludedField = null) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri
                ?? throw new InvalidOperationException("The audit request URI is missing.");
            Paths.Add(requestUri.PathAndQuery);
            var auditJson = AuditJson(excludedField);
            var responseBody = requestUri.AbsolutePath.EndsWith("/audit-a", StringComparison.Ordinal)
                ? auditJson
                : $"[{auditJson}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }

    private static readonly (string Name, string Json)[] AuditFields =
    [
        ("auditEventId", "\"auditEventId\":\"audit-a\""),
        ("projectId", "\"projectId\":\"project-a\""),
        ("actor", "\"actor\":\"user-a\""),
        ("action", "\"action\":\"repository.created\""),
        ("target", "\"target\":\"repository-a\""),
        ("risk", "\"risk\":\"normal\""),
        ("source", "\"source\":\"dashboard\""),
        ("occurredAt", "\"occurredAt\":\"2026-08-30T00:00:00Z\"")
    ];

    private static string AuditJson(string? excludedField) =>
        $"{{{string.Join(',', AuditFields
            .Where(field => field.Name != excludedField)
            .Select(static field => field.Json))}}}";
}
