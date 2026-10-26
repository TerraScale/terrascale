using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class OperatorApiKeyTests
{
    private static readonly string[] ReadScopes = ["documents:read"];

    [Test]
    public async Task An_operator_key_with_an_expiry_stops_authenticating_when_it_expires()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var expires = time.GetUtcNow().AddDays(90);
        using var issued = await api.Server.Client.PostAsJsonAsync(ManagementApi.Uri("/admin/api-keys"),
            new { name = "ci-e2e", scopes = ReadScopes, organizationId = database.Organization, projectId = database.Project, expiresAt = expires });
        var body = await issued.Content.ReadFromJsonAsync<JsonElement>();
        using var data = api.DataClient(body.GetProperty("secret").GetString());
        var collections = ManagementApi.Uri($"/v1/projects/{database.Project}/databases/{database.Name}/collections");

        using var before = await data.GetAsync(collections);
        // Move UTC without replaying 90 days of the host's background timers.
        time.AdjustTime(expires);
        using var after = await data.GetAsync(collections);
        using var past = await api.Server.Client.PostAsJsonAsync(ManagementApi.Uri("/admin/api-keys"),
            new { name = "past", scopes = ReadScopes, organizationId = database.Organization, expiresAt = time.GetUtcNow().AddMinutes(-1) });
        using var tooFar = await api.Server.Client.PostAsJsonAsync(ManagementApi.Uri("/admin/api-keys"),
            new { name = "far", scopes = ReadScopes, organizationId = database.Organization, expiresAt = time.GetUtcNow().AddDays(367) });

        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(body.GetProperty("expiresAt").GetDateTimeOffset()).IsEqualTo(expires);
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(past.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(tooFar.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    [Arguments("organization")]
    [Arguments("project")]
    [Arguments("database")]
    public async Task Only_the_operator_listener_can_revoke_a_key_and_its_next_request_is_refused(string scope)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var created = await api.Server.Client.PostAsJsonAsync(ManagementApi.Uri("/admin/api-keys"),
            new
            {
                name = "operator",
                scopes = ReadScopes,
                organizationId = database.Organization,
                projectId = scope == "organization" ? null : database.Project,
                databaseId = scope == "database" ? database.Id : null,
            });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var issued = await created.Content.ReadFromJsonAsync<JsonElement>();
        using var reader = api.DataClient(issued.GetProperty("secret").GetString());
        var target = ManagementApi.Uri(database.Data + "/collections");
        using var before = await reader.GetAsync(target);
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var revoke = ManagementApi.Uri($"/admin/api-keys/{issued.GetProperty("id").GetString()}");
        using var wrongListener = await reader.DeleteAsync(revoke);
        await Assert.That(wrongListener.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var stillValid = await reader.GetAsync(target);
        await Assert.That(stillValid.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var revoked = await api.Server.Client.DeleteAsync(revoke);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var after = await reader.GetAsync(target);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var repeated = await api.Server.Client.DeleteAsync(revoke);
        await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await ErrorAssert.ErrorAsync(api.Server.Client.DeleteAsync(ManagementApi.Uri("/admin/api-keys/malformed")),
            HttpStatusCode.BadRequest, "invalid_request", "key must be a canonical uppercase ULID");
        using var removedDatabase = await api.Client.DeleteAsync(ManagementApi.Uri(database.Path));
        await Assert.That(removedDatabase.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var removedProject = await api.Client.DeleteAsync(ManagementApi.Uri(database.ProjectPath));
        await Assert.That(removedProject.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }
}
