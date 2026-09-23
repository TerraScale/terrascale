using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.FastTests;

/// <summary>Every member-facing page must render with its heading —
/// a 500 or missing title on any of these blocks the whole area.</summary>
public sealed class PageSmokeTests
{
    private static readonly (string Path, string Heading)[] MemberPages =
    [
        ("/", "Overview"),
        ("/databases", "Databases"),
        ("/data", "Tables"),
        ("/api-keys", "API keys"),
        ("/access", "Team"),
        ("/usage", "Usage"),
        ("/audit", "Logs"),
        ("/notifications", "Notifications"),
        ("/settings", "Settings"),
        ("/profile", "Profile"),
        ("/organization", "Organization"),
    ];

    [Test]
    public async Task EveryMemberPageRendersWithItsHeading()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-smoke-{Guid.NewGuid():N}@example.test");
        var failures = new List<string>();
        foreach (var (path, heading) in MemberPages)
        {
            var page = await app.GetPageAsync(client, path);
            if (page.StatusCode != 200 || page.Heading != heading)
            {
                failures.Add($"{path}: {page.StatusCode} '{page.Heading}' (expected 200 '{heading}')");
            }
        }

        await Assert.That(string.Join("; ", failures)).IsEqualTo("");
    }

    [Test]
    public async Task SettingsStaysInAccountMenuButNotSidebar()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-nav-{Guid.NewGuid():N}@example.test");
        var page = await app.GetPageAsync(client, "/");

        await Assert.That(page.NavItems.Contains("Settings")).IsFalse();
        await Assert.That(page.Contains("class=\"ts-menu-item\" href=\"/settings\"")).IsTrue();
    }
}
