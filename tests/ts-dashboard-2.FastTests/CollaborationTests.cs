using System.Net;
using TerraScale.Dashboard2.Testing;

namespace TerraScale.Dashboard2.FastTests;

/// <summary>Programmatic access and collaboration surfaces: keys, invites,
/// and the notification inbox a user acts from.</summary>
public sealed class CollaborationTests
{
    [Test]
    public async Task ApiKeyCreateShowsUpInTheCredentialsList()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-key-{Guid.NewGuid():N}@example.test");
        var keyName = $"ci-{Guid.NewGuid():N}"[..18];

        using var created = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/credentials/key", new Dictionary<string, string>
        {
            ["name"] = keyName,
            ["scopes"] = "documents:read",
        });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Redirect);

        var page = await app.GetPageAsync(client, "/api-keys");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("API keys");
        await Assert.That(page.Contains(keyName)).IsTrue();
    }

    [Test]
    public async Task TeamInviteListsTheInvitation()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-team-{Guid.NewGuid():N}@example.test");
        var invitee = $"invitee-{Guid.NewGuid():N}@example.test";

        using var invited = await DashboardTestApp.PostFormAsync(client, "/api/v1/access/invitations", new Dictionary<string, string>
        {
            ["email"] = invitee,
        });
        await Assert.That(invited.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(invited.Headers.Location?.ToString()).IsEqualTo("/access");

        var page = await app.GetPageAsync(client, "/access");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Team");
        await Assert.That(page.Contains(invitee)).IsTrue();
    }

    [Test]
    public async Task NotificationsPageRendersMarkAllRead()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-notes-{Guid.NewGuid():N}@example.test");

        var page = await app.GetPageAsync(client, "/notifications");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Notifications");
        await Assert.That(page.Contains("Mark all read")).IsTrue();
        await Assert.That(page.Contains("/api/v1/notifications/read-all")).IsTrue();

        using var readAll = await DashboardTestApp.PostFormAsync(client, "/api/v1/notifications/read-all", new Dictionary<string, string>());
        await Assert.That(readAll.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(readAll.Headers.Location?.ToString()).IsEqualTo("/notifications");
    }
}
