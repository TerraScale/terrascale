using System.Net;
using System.Text.Json;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ManagementUserRouteTests
{
    [Test]
    public async Task The_operator_serving_as_a_user_reads_and_backs_up_by_database_name_on_the_internal_listener(CancellationToken token)
    {
        await using var host = await SqlBackupHost.StartAsync(token);
        var byName = $"/v1/projects/{host.Database.Project}/databases/{host.Database.Name}";
        using var service = host.Api.Service(host.Api.User);

        using var database = await service.GetAsync(ManagementApi.Uri(byName), token);
        await Assert.That(database.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await database.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("name").GetString())
            .IsEqualTo(host.Database.Name);

        using var destination = await service.GetAsync(ManagementApi.Uri($"/v1/projects/{host.Database.Project}/backup-destination"), token);
        await ErrorAssert.ErrorAsync(destination, HttpStatusCode.NotFound, "not_found", "no backup destination is set for this project; set one with setBackupDestination");

        using var usage = await service.GetAsync(ManagementApi.Uri($"{byName}/usage"), token);
        await Assert.That(usage.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var backup = await service.PostAsync(ManagementApi.Uri($"{byName}/backups"), content: null, token);
        await Assert.That(backup.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var backupId = (await backup.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString();
        await Assert.That(backup.Headers.Location!.ToString()).IsEqualTo($"{byName}/backups/{backupId}");
    }

    [Test]
    public async Task A_user_outside_the_projects_organization_answers_like_an_unknown_project(CancellationToken token)
    {
        await using var host = await SqlBackupHost.StartAsync(token);
        using var intruder = host.Api.As(EntityIds.New().ToString());
        using var foreign = await intruder.GetAsync(ManagementApi.Uri($"/v1/projects/{host.Database.Project}/databases/{host.Database.Name}"), token);
        await ErrorAssert.ErrorAsync(foreign, HttpStatusCode.NotFound, "unknown_database", "the database does not exist");

        using var unknown = await intruder.GetAsync(ManagementApi.Uri($"/v1/projects/{EntityIds.New()}/databases/{EntityIds.New()}"), token);
        await ErrorAssert.ErrorAsync(unknown, HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
    }
}
