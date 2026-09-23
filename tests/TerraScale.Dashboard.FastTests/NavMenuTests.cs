using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.FastTests;

/// <summary>DSH-135 / DSH-145: Profile and Notifications are account-level
/// pages, so the sidebar nav menu must not list them. They stay reachable
/// from the account menu and keep their route metadata (page title).</summary>
public sealed class NavMenuTests
{
    [Test]
    public async Task SidebarNavMenuOmitsProfileAndNotifications()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-navmenu-{Guid.NewGuid():N}@example.test");

        var page = await app.GetPageAsync(client, "/");

        await Assert.That(page.NavItems.Contains("Profile")).IsFalse();
        await Assert.That(page.NavItems.Contains("Notifications")).IsFalse();
    }

    [Test]
    public async Task AccountMenuStillOffersProfileAndNotifications()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-accountmenu-{Guid.NewGuid():N}@example.test");

        var page = await app.GetPageAsync(client, "/");

        await Assert.That(page.AccountMenuItems.Contains("Profile")).IsTrue();
        await Assert.That(page.AccountMenuItems.Contains("Notifications")).IsTrue();
    }

    [Test]
    public async Task HiddenRoutesKeepTheirPageTitle()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-hiddenroutes-{Guid.NewGuid():N}@example.test");

        var profile = await app.GetPageAsync(client, "/profile");
        var notifications = await app.GetPageAsync(client, "/notifications");

        await Assert.That(profile.Heading).IsEqualTo("Profile");
        await Assert.That(notifications.Heading).IsEqualTo("Notifications");
    }
}
