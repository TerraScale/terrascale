using TerraBase.Primitives;

namespace TerraBase.Testing;

internal sealed record SqlTenantFixture(string User, string Organization, string Project, string Writer, string Reader)
{
    private static readonly Lazy<Task<SqlTenantFixture>> Shared = new(CreateAsync);

    internal static Task<SqlTenantFixture> GetAsync() => Shared.Value;

    internal async Task<(ManagementApi Api, ManagedDatabaseRef Database)> CreateDatabaseAsync()
    {
        var api = await ManagementApi.StartSharedAsAsync(User);
        try
        {
            var name = "sql_" + EntityIds.New().ToString().ToLowerInvariant();
            return (api, await api.CreateManagedDatabaseAsync(Organization, Project, name));
        }
        catch
        {
            await api.DisposeAsync();
            throw;
        }
    }

    private static async Task<SqlTenantFixture> CreateAsync()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        var organization = await api.CreateOrganizationAsync("SQL fixture");
        await api.SetPlanAsync(api.User, organization, "enterprise");
        var project = await api.CreateProjectAsync(organization, "SQL journeys");
        var writer = await api.Server.IssueNarrowedAsync(organization, project, null, "sql:read", "sql:write");
        var reader = await api.Server.IssueNarrowedAsync(organization, project, null, "sql:read");
        return new(api.User, organization, project, writer, reader);
    }
}
