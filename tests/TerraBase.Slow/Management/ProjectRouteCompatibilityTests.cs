using System.Net;
using System.Text.Json;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class ProjectRouteCompatibilityTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Backup_locations_preserve_database_names_and_legacy_ids_for_matched_route_spellings(bool legacy)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var client = legacy ? api.As(api.User) : ProjectClient(api, database.Project);
        var path = legacy
            ? $"/V1/Management/Organizations/{database.Organization}/Projects/{database.Project}/DataBases/{database.Id}/Backups/"
            : $"/V1/Projects/{database.Project}/DataBases/{database.Name}/Backups/";
        using var created = await client.PostAsync(ManagementApi.Uri(path), null);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
        var backup = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        await Assert.That(created.Headers.Location?.OriginalString).IsEqualTo($"{(legacy ? database.Path : database.Data)}/backups/{backup}");

        var listed = await client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(path));
        await Assert.That(listed.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("id").GetString() == backup)).IsTrue();
    }

    [Test]
    public async Task Missing_saved_queries_answer_coded_uncached_errors_for_project_tokens()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var client = ProjectClient(api, database.Project);
        var path = ManagementApi.Uri($"/v1/projects/{database.Project}/saved-queries/{EntityIds.New()}");

        await ErrorAssert.ErrorAsync(client.GetAsync(path), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(client.PutAsJsonAsync(path, new { name = "Missing", sql = "SELECT * FROM people" }), HttpStatusCode.NotFound, "not_found");
        await ErrorAssert.ErrorAsync(client.DeleteAsync(path), HttpStatusCode.NotFound, "not_found");
    }

    private static HttpClient ProjectClient(ManagementApi api, string project) => api.WithToken(api.Issuer.Mint(api.User, token =>
    {
        token.Claims["project_id"] = project;
        token.Claims["scope"] = "databases:read databases:write";
    }));
}
