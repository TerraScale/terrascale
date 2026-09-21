using System.Net;
using System.Net.Http.Json;
using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.FastTests;

/// <summary>v1 parity guards: the member nav is exactly the eleven v1 items,
/// the removed /backups route stays gone, and the e2e-only owner promotion
/// shim works through the harness that enables it.</summary>
public sealed class SecurityGateTests
{
    private static readonly string[] V1NavItems =
    [
        "Overview", "Databases", "Tables", "API keys", "Team", "Usage", "Logs",
        "Notifications", "Settings", "Profile", "Organization",
    ];

    [Test]
    public async Task MemberNavHasExactlyTheElevenV1Items()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-nav-{Guid.NewGuid():N}@example.test");
        var page = await app.GetPageAsync(client, "/");
        await Assert.That(string.Join("|", page.NavItems)).IsEqualTo(string.Join("|", V1NavItems));
    }

    [Test]
    public async Task BackupsRouteIsGone()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-nav-{Guid.NewGuid():N}@example.test");
        var page = await app.GetPageAsync(client, "/backups");
        await Assert.That(page.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task DevShimPromoteOwnerWorksThroughTheHarness()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-owner-{Guid.NewGuid():N}@example.test");
        using var promote = await client.PostAsync("/api/v1/testing/dev-shim/reset",
            JsonContent.Create(new { promoteOwner = true }));
        await Assert.That(promote.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var admin = await client.GetAsync("/admin/users");
        await Assert.That(admin.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = HtmlPage.Capture((int)admin.StatusCode, await admin.Content.ReadAsStringAsync());
        await Assert.That(page.Heading).IsEqualTo("Users");
        await Assert.That(page.Contains("Owner-only user administration")).IsTrue();
    }
}
