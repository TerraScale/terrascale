using System.Net;

namespace TerraBase.Fast.Tenancy;

public sealed class TenantBoundaryTests
{
    [Test]
    public async Task Another_tenant_cannot_read_or_write_an_existing_database()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        using var owner = await api.CreateTenantAsync("sql:read", "sql:write");
        using var outsider = await api.CreateTenantAsync("sql:read", "sql:write");
        using var dashboard = api.As(owner.User);
        using var otherDashboard = api.As(outsider.User);
        var database = await api.CreateDatabaseAsync(owner.OrganizationId, owner.ProjectId, "private", dashboard);
        await api.CreateDatabaseAsync(outsider.OrganizationId, outsider.ProjectId, "own", otherDashboard);
        var path = $"/v1/projects/{owner.ProjectId}/databases/private";
        using var seeded = await owner.Data.PostAsJsonAsync(ManagementApi.Uri(path + "/sql/execute"), new
        {
            statements = new[]
            {
                new { sql = "CREATE TABLE notes (value INTEGER)" },
                new { sql = "INSERT INTO notes VALUES (42)" },
            },
        });
        seeded.EnsureSuccessStatusCode();
        using var admitted = await ManagementApi.SqlAsync(outsider.Data,
            $"/v1/projects/{outsider.ProjectId}/databases/own", "SELECT 1");
        admitted.EnsureSuccessStatusCode();

        var managementPath = ManagementApi.Uri($"{path}");
        using var ownerManagement = api.WithToken(api.ProjectToken(owner.ProjectId, "databases:read"));
        using var visible = await ownerManagement.GetAsync(managementPath);
        visible.EnsureSuccessStatusCode();

        using var read = await ManagementApi.SqlAsync(outsider.Data, path, "SELECT * FROM notes");
        using var write = await ManagementApi.SqlAsync(outsider.Data, path, "INSERT INTO notes VALUES (99)");
        using var intruderManagement = api.WithToken(api.ProjectToken(outsider.ProjectId, "databases:read"));
        using var management = await intruderManagement.GetAsync(managementPath);

        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(write.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(management.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var rows = await ManagementApi.RowsAsync(owner.Data, path, "SELECT value FROM notes");
        await Assert.That(rows.Length).IsEqualTo(1);
        await Assert.That(rows[0][0].GetInt64()).IsEqualTo(42);
    }
}
