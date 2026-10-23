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
    [Arguments(200, "accepted", Closure, true)]
    [Arguments(202, "pending", Closure, false)]
    public async Task Closure_answers_report_their_status_and_send_no_user(int status, string state, string closure, bool accepted)
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            sent = request;
            body = await request.Content!.ReadAsStringAsync(token);
            var response = TerraBaseIntegrationFixture.Json($$"""{"status":"{{state}}","closureId":"{{closure}}","pendingHolders":{{(accepted ? 0 : 1)}}}""");
            response.StatusCode = (System.Net.HttpStatusCode)status;
            return response;
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = new Uri("http://100.64.0.1:8081") });

        var answer = await client.CloseProjectAsync(Project, Organization, Closure);
        await Assert.That(answer.Accepted).IsEqualTo(accepted);
        await Assert.That(sent!.RequestUri!.AbsolutePath).IsEqualTo($"/admin/projects/{Project}/closure");
        await Assert.That(sent.Headers.Contains("X-TerraBase-User-Id")).IsFalse();
        await Assert.That(body).IsEqualTo($$"""{"version":1,"closureId":"{{Closure}}","authorityOrganizationId":"{{Organization}}"}""");
    }

    // A success body that answers another closure, or calls a 202 accepted, proves nothing.
    [Test]
    [Arguments(200, "accepted", "01JA0000000000000000000008")]
    [Arguments(202, "accepted", Closure)]
    [Arguments(200, "pending", Closure)]
    public async Task Mismatched_closure_answers_are_invalid(int status, string state, string closure)
    {
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            var response = TerraBaseIntegrationFixture.Json($$"""{"status":"{{state}}","closureId":"{{closure}}","pendingHolders":0}""");
            response.StatusCode = (System.Net.HttpStatusCode)status;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = new Uri("http://100.64.0.1:8081") });

        var error = await Assert.ThrowsAsync<TerraBaseException>(() => client.CloseProjectAsync(Project, Organization, Closure));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }
}
