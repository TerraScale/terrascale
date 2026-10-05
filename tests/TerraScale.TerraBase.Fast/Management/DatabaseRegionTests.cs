using System.Net;
using System.Text.Json;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Management;

public sealed class DatabaseRegionTests
{
    [Test]
    public async Task Database_responses_report_the_current_node_region_after_restart()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-database-region-").FullName;
        try
        {
            string user;
            string databases;
            string database;
            string branch;
            await using (var api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = "gru-1";
                settings["data-directory"] = directory;
            }))
            {
                user = api.User;
                var organization = await api.CreateOrganizationAsync("Region tests");
                var project = await api.CreateProjectAsync(organization, "Main");
                databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
                using var created = await api.Client.PostAsJsonAsync(new Uri(databases, UriKind.Relative), new { name = "main" });
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
                var body = await created.Content.ReadFromJsonAsync<JsonElement>();
                database = body.GetProperty("id").GetString()!;
                await Assert.That(body.GetProperty("region").GetString()).IsEqualTo("gru-1");
                await Assert.That(body.GetProperty("projectId").GetString()).IsEqualTo(project);

                using var branched = await api.Client.PostAsJsonAsync(new Uri($"{databases}/{database}/branches", UriKind.Relative), new { name = "preview" });
                await Assert.That(branched.StatusCode).IsEqualTo(HttpStatusCode.Created);
                var branchBody = await branched.Content.ReadFromJsonAsync<JsonElement>();
                branch = branchBody.GetProperty("id").GetString()!;
                await Assert.That(branchBody.GetProperty("region").GetString()).IsEqualTo("gru-1");
                await Assert.That(branchBody.GetProperty("branchedFrom").GetString()).IsEqualTo(database);
                await AssertDatabaseRegionsAsync(api.Client, databases, database, branch, "gru-1");
            }

            await using var restarted = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = "iad-1";
                settings["data-directory"] = directory;
            });
            using var owner = restarted.As(user);
            await AssertDatabaseRegionsAsync(owner, databases, database, branch, "iad-1");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertDatabaseRegionsAsync(HttpClient client, string databases, string database, string branch, string region)
    {
        var list = await client.GetFromJsonAsync<JsonElement>(new Uri(databases, UriKind.Relative));
        var items = list.GetProperty("items").EnumerateArray().ToList();
        await Assert.That(items.Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo([database, branch]);
        foreach (var item in items)
        {
            await Assert.That(item.GetProperty("region").GetString()).IsEqualTo(region);
        }

        foreach (var id in new[] { database, branch })
        {
            using var response = await client.GetAsync(new Uri($"{databases}/{id}", UriKind.Relative));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Headers.GetValues("X-TerraBase-Region")).IsEquivalentTo([region]);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(body.GetProperty("id").GetString()).IsEqualTo(id);
            await Assert.That(body.GetProperty("region").GetString()).IsEqualTo(region);
        }
    }
}
