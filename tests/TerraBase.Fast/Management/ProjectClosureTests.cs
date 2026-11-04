using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

/// <summary>ts-auth's project closure handoff (docs/terrabase/project-closure.md) on one machine.</summary>
public sealed class ProjectClosureTests
{
    private static readonly string[] ReadScope = ["documents:read"];

    // Review R1: a receipt binds to its fence by closure id, not by comparing two machines' clocks. A fence recorded by a
    // machine whose clock ran ahead is still accepted once every holder confirmed it.
    [Test]
    public async Task Account_erasure_revokes_existing_project_keys_and_the_current_backup_destination()
    {
        var s3 = new FakeS3();
        await using var api = await ManagementApi.StartAsync(s3.Configure, s3.Route);
        var database = await api.CreateManagedDatabaseAsync();
        var route = $"/v1/management/organizations/{database.Organization}/projects/{database.Project}";
        using var issued = await api.Server.Client.PostAsJsonAsync(ManagementApi.Uri("/admin/api-keys"),
            new { name = "existing", scopes = ReadScope, organizationId = database.Organization, projectId = database.Project });
        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var key = await issued.Content.ReadFromJsonAsync<JsonElement>();
        using var data = api.DataClient(key.GetProperty("secret").GetString());
        using var before = await ManagementApi.SqlAsync(data, database, "SELECT 1");
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var destination = await api.Client.PutAsJsonAsync(ManagementApi.Uri(route + "/backup-destination"), new Dictionary<string, string>
        {
            ["endpoint"] = FakeS3.CustomerEndpoint,
            ["region"] = "test-region-1",
            ["bucket"] = FakeS3.CustomerBucket,
            ["accessKeyId"] = FakeS3.CustomerAccessKey,
            ["secretKey"] = FakeS3.CustomerSecretKey,
            ["mode"] = "customer",
        });
        await Assert.That(destination.IsSuccessStatusCode).IsTrue().Because(await destination.Content.ReadAsStringAsync());
        var closure = EntityIds.New().ToString();
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var response = await PutAsync(api.Server.Client, database.Project, closure, database.Organization, 2, token);
            await Assert.That(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted).IsTrue();
            return response.StatusCode == HttpStatusCode.OK;
        }, timeout: TimeSpan.FromSeconds(60));
        using var revoked = await ManagementApi.SqlAsync(data, database, "SELECT 1");
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var catalog = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var transaction = await catalog.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await transaction.GetAsync("api_keys", key.GetProperty("id").GetString()!, CancellationToken.None)).IsNull();
            foreach (var collection in new[] { $"projects/{database.Project}/api_keys", $"projects/{database.Project}/backup_destination" })
            {
                await Assert.That((await transaction.ListAsync(collection, null, 10, 1024 * 1024, CancellationToken.None)).Records).IsEmpty().Because(collection);
            }
        }

        await CloseAsync(api.Server.Client, database.Project, closure, database.Organization, HttpStatusCode.OK, 2);
    }

    // Every pass converges every closure ever accepted. Written under SYSTEM's writer, settled ones held it ~30 times a second
    // on every node (2026-10-27); now they are read, and one that has something to erase again is still written.
    private static async Task<JsonElement> CloseAsync(HttpClient client, string project, string closure, string organization, HttpStatusCode expected, int version = 1)
    {
        using var response = await PutAsync(client, project, closure, organization, version);
        await Assert.That(response.StatusCode).IsEqualTo(expected).Because(await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string project, string closure, string organization, int version = 1, CancellationToken cancellationToken = default) =>
        client.PutAsJsonAsync(ManagementApi.Uri($"/admin/projects/{project}/closure"), new { version, closureId = closure, authorityOrganizationId = organization }, cancellationToken);
}
