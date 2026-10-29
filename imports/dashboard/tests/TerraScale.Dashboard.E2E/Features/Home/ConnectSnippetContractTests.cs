using System.Text.Json;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Features.Home;

namespace TerraScale.Dashboard.E2E.Features.Home;

[ClassDataSource<DashboardRootFixture>]
public sealed class ConnectSnippetContractTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task SdkPinConstantsAndInstallCommandsMatchTheSharedVersionContract()
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "sdk-versions.json")));
        var versions = manifest.RootElement;
        var snippets = HomeConnectSnippets.For("project", "app").ToDictionary(snippet => snippet.Id);
        foreach (var pin in new[]
        {
            (Key: "javascript", Id: "js", Version: SdkVersions.JavaScript),
            (Key: "go", Id: "go", Version: SdkVersions.Go),
            (Key: "python", Id: "python", Version: SdkVersions.Python),
            (Key: "rust", Id: "rust", Version: SdkVersions.Rust)
        })
        {
            await Assert.That(pin.Version).IsEqualTo(versions.GetProperty(pin.Key).GetString());
            await Assert.That(snippets[pin.Id].Install).Contains(pin.Version);
        }
        // No copyable sample for the unpublished NuGet package; the page links its source quickstart.
        await Assert.That(snippets.ContainsKey("csharp")).IsFalse();
    }

    [Test]
    [Arguments("app", "app")]
    [Arguments("orders & sales/2026", "orders%20%26%20sales%2F2026")]
    public async Task BothPostgresSurfacesMatchTheDocumentedDatabaseOnlyPath(string database, string escaped)
    {
        // ts-docs docs/guides/postgres-clients.md: the path is the database name within the API key's project.
        // Project identity belongs to the API key password, never a project/database path segment.
        var expected = $"postgresql://app@pg.terrascale.tech:5432/{escaped}?sslmode=verify-full&sslrootcert=system";
        var connection = DatabaseConnections.For("different-project", database).Single(item => item.Id == "postgres");
        await Assert.That(connection.Url).IsEqualTo(expected);
        await Assert.That(HomeConnectSnippets.Postgres(database)).Contains($"psql \"{expected}\"");
        await Assert.That(connection.Url).DoesNotContain("different-project");
        await Assert.That(connection.Hint).Contains("libpq 16+");
    }
}
