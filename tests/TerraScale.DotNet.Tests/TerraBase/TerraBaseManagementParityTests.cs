using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseManagementParityTests
{
    [Test]
    [Arguments("pending")]
    [Arguments("accepted")]
    [Arguments("declined")]
    [Arguments("revoked")]
    public async Task Invitation_pages_preserve_pending_and_terminal_statuses(string status)
    {
        var json = $$$"""
            {"items":[{"id":"01JA0000000000000000000002","email":"synthetic@example.test","role":"member","invitedBy":"01JA0000000000000000000003",
             "createdAt":"2026-10-01T00:00:00Z","expiresAt":"2026-10-08T00:00:00Z","status":"{{{status}}}"}
            ],"next":null}
            """;
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(TerraBaseIntegrationFixture.Json(json)));
        using var http = new HttpClient(handler);
        var client = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions
        {
            BaseAddress = new Uri("https://api.example.test"),
            AccessTokenProvider = _ => ValueTask.FromResult("project-user-token"),
        });

        var page = await client.ListInvitationsAsync("01JA0000000000000000000003", TerraBaseIntegrationFixture.ProjectId);
        await Assert.That(page.Items.Single().Status).IsEqualTo(status);
    }
}

public sealed class TerraBaseProjectClosureTests
{
    private const string Project = "01JA0000000000000000000005";
    private const string Organization = "01JA0000000000000000000006";
    private const string Closure = "01JA0000000000000000000007";

    [Test]
    [Arguments(1, 200, "accepted", Closure, true, null)]
    [Arguments(1, 202, "pending", Closure, false, null)]
    [Arguments(2, 200, "accepted", Closure, true, null)]
    [Arguments(2, 202, "pending", Closure, false, null)]
    [Arguments(1, 200, "accepted", Closure, true, 2)]
    [Arguments(1, 202, "pending", Closure, false, 2)]
    [Arguments(2, 200, "accepted", Closure, true, 2)]
    [Arguments(2, 202, "pending", Closure, false, 2)]
    public async Task Closure_answers_report_their_status_and_send_no_user(int version, int status, string state, string closure, bool accepted, int? evidenceVersion)
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            sent = request;
            body = await request.Content!.ReadAsStringAsync(token);
            var evidence = evidenceVersion.HasValue ? ",\"evidenceVersion\":2" : string.Empty;
            var response = TerraBaseIntegrationFixture.Json($$"""{"status":"{{state}}","closureId":"{{closure}}","pendingHolders":{{(accepted ? 0 : 1)}}{{evidence}}}""");
            response.StatusCode = (System.Net.HttpStatusCode)status;
            return response;
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = new Uri("http://100.64.0.1:8081") });

        var answer = await SendClosureAsync(client, version);
        await Assert.That(answer.Accepted).IsEqualTo(accepted);
        await Assert.That(answer.ClosureId).IsEqualTo(Closure);
        await Assert.That(answer.PendingHolders).IsEqualTo(accepted ? 0 : 1);
        await Assert.That(answer.EvidenceVersion).IsEqualTo(evidenceVersion ?? 0);
        await Assert.That(sent!.Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(sent!.RequestUri!.AbsolutePath).IsEqualTo($"/admin/projects/{Project}/closure");
        await Assert.That(sent.Headers.Contains("X-TerraBase-User-Id")).IsFalse();
        await Assert.That(sent.Headers.Contains("X-TerraBase-Organization-Id")).IsFalse();
        await Assert.That(sent.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        await Assert.That(body).IsEqualTo($$"""{"version":{{version}},"closureId":"{{Closure}}","authorityOrganizationId":"{{Organization}}"}""");
    }

    // A success body that answers another closure, or calls a 202 accepted, proves nothing.
    [Test]
    [Arguments(1, 200, "accepted", "01JA0000000000000000000008")]
    [Arguments(1, 202, "accepted", Closure)]
    [Arguments(1, 200, "pending", Closure)]
    [Arguments(2, 200, "accepted", "01JA0000000000000000000008")]
    [Arguments(2, 202, "pending", "01JA0000000000000000000008")]
    [Arguments(2, 202, "accepted", Closure)]
    [Arguments(2, 200, "pending", Closure)]
    [Arguments(2, 201, "accepted", Closure)]
    [Arguments(2, 200, "unknown", Closure)]
    public async Task Mismatched_closure_answers_are_invalid(int version, int status, string state, string closure)
    {
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            var response = TerraBaseIntegrationFixture.Json($$"""{"status":"{{state}}","closureId":"{{closure}}","pendingHolders":0}""");
            response.StatusCode = (System.Net.HttpStatusCode)status;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = new Uri("http://100.64.0.1:8081") });

        var error = await Assert.ThrowsAsync<TerraBaseException>(() => SendClosureAsync(client, version));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    [Arguments(1, "not_empty")]
    [Arguments(1, "closure_conflict")]
    [Arguments(2, "closure_conflict")]
    [Arguments(2, "binding_conflict")]
    public async Task Closure_conflicts_are_preserved_without_retry(int version, string code)
    {
        var requests = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json($$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""", System.Net.HttpStatusCode.Conflict));
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = new Uri("http://100.64.0.1:8081") });

        var error = await Assert.ThrowsAsync<TerraBaseConflictException>(() => SendClosureAsync(client, version));
        await Assert.That(error!.ErrorCode).IsEqualTo(code);
        await Assert.That(requests).IsEqualTo(1);
    }

    private static Task<TerraBaseProjectClosure> SendClosureAsync(TerraBaseManagementClient client, int version) =>
        version == 1 ? client.CloseProjectAsync(Project, Organization, Closure) : client.EraseProjectAsync(Project, Organization, Closure);
}

public sealed class TerraBaseHistoryErasureTests
{
    private const string Erasure = "01JA0000000000000000000009";

    [Test]
    [Arguments(200, "complete", true)]
    [Arguments(202, "reverify", false)]
    public async Task History_erasure_answers_report_their_status_and_send_no_user(int status, string state, bool complete)
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            sent = request;
            body = await request.Content!.ReadAsStringAsync(token);
            var response = TerraBaseIntegrationFixture.Json($$"""
                {"status":"{{state}}","erasureId":"{{Erasure}}","pendingHolders":0,"activeDueAt":"2026-11-22T00:00:00Z","backupDueAt":"2026-12-22T00:00:00Z",
                 "activeOverdue":false,"backupOverdue":false,"settlementToken":{{(complete ? "null" : "\"01JA000000000000000000000A\"")}}}
                """);
            response.StatusCode = (System.Net.HttpStatusCode)status;
            return response;
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = new Uri("http://100.64.0.1:8081") });

        var answer = await client.RequestHistoryErasureAsync(Erasure, new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero));
        await Assert.That(answer.Complete).IsEqualTo(complete);
        await Assert.That(answer.SettlementToken is null).IsEqualTo(complete);
        await Assert.That(sent!.RequestUri!.AbsolutePath).IsEqualTo($"/admin/history-erasures/{Erasure}");
        await Assert.That(sent.Headers.Contains("X-TerraBase-User-Id")).IsFalse();
        await Assert.That(body).IsEqualTo("""{"version":1,"requestedAt":"2026-10-23T00:00:00+00:00"}""");
    }

    // A body for another erasure, or a status contradicting its code, proves nothing.
    [Test]
    [Arguments(200, "rewriting", Erasure)]
    [Arguments(202, "complete", Erasure)]
    [Arguments(200, "complete", "01JA0000000000000000000008")]
    public async Task Mismatched_history_erasure_answers_are_invalid(int status, string state, string erasure)
    {
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            var response = TerraBaseIntegrationFixture.Json($$"""
                {"status":"{{state}}","erasureId":"{{erasure}}","pendingHolders":0,"activeDueAt":"2026-11-22T00:00:00Z","backupDueAt":"2026-12-22T00:00:00Z","activeOverdue":false,"backupOverdue":false}
                """);
            response.StatusCode = (System.Net.HttpStatusCode)status;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = new Uri("http://100.64.0.1:8081") });

        var error = await Assert.ThrowsAsync<TerraBaseException>(() => client.RequestHistoryErasureAsync(Erasure, DateTimeOffset.UnixEpoch));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }
}
