using System.Net;
using System.Text.RegularExpressions;
using TerraScale.Dashboard2.Testing;

namespace TerraScale.Dashboard2.FastTests;

/// <summary>The core product loop: a user must be able to create a database,
/// see it listed, open its detail page, and delete it.</summary>
public sealed partial class DatabaseLifecycleTests
{
    [Test]
    public async Task CreateDatabaseListsItAndRendersDetail()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-db-{Guid.NewGuid():N}@example.test");
        var name = $"orders-{Guid.NewGuid():N}"[..24];

        using var created = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/create", new Dictionary<string, string> { ["name"] = name });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(created.Headers.Location?.ToString()).IsEqualTo("/databases");

        var list = await app.GetPageAsync(client, "/databases");
        await Assert.That(list.StatusCode).IsEqualTo(200);
        await Assert.That(list.Contains(name)).IsTrue();
        var databaseId = DatabaseId(list.Html, name);

        var detail = await app.GetPageAsync(client, $"/databases/{databaseId}");
        await Assert.That(detail.StatusCode).IsEqualTo(200);
        await Assert.That(detail.Contains(name)).IsTrue();
        await Assert.That(detail.Contains("Placement and latency")).IsTrue();
    }

    [Test]
    public async Task DeleteDatabaseRequiresExactNameAndThenRemovesIt()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-db-{Guid.NewGuid():N}@example.test");
        var name = $"orders-{Guid.NewGuid():N}"[..24];
        await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/create", new Dictionary<string, string> { ["name"] = name });
        var list = await app.GetPageAsync(client, "/databases");
        var databaseId = DatabaseId(list.Html, name);

        // A mistyped confirmation must not delete the database.
        using var rejected = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new Dictionary<string, string>
        {
            ["databaseId"] = databaseId,
            ["confirm"] = "wrong-name",
        });
        await Assert.That(rejected.Headers.Location?.ToString()).IsEqualTo($"/databases?delete={databaseId}");
        var stillThere = await app.GetPageAsync(client, "/databases");
        await Assert.That(stillThere.Contains(name)).IsTrue();

        using var deleted = await DashboardTestApp.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new Dictionary<string, string>
        {
            ["databaseId"] = databaseId,
            ["confirm"] = name,
        });
        await Assert.That(deleted.Headers.Location?.ToString()).IsEqualTo("/databases");
        var empty = await app.GetPageAsync(client, "/databases");
        await Assert.That(empty.Contains(name)).IsFalse();
        await Assert.That(empty.Contains("No databases saved")).IsTrue();
    }

    /// <summary>Pulls the row's detail link id for a freshly created database name.</summary>
    private static string DatabaseId(string html, string name) =>
        Regex.Match(html, $"href=\"/databases/(?<id>[^\"]+)\" title=\"{Regex.Escape(name)}\"").Groups["id"].Value is { Length: > 0 } id
            ? id
            : throw new InvalidOperationException($"No database row link found for {name}");
}
