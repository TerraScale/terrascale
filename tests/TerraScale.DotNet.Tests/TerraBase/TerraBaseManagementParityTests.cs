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
