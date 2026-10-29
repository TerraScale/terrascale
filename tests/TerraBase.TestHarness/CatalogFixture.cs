using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Testing;

internal static class CatalogFixture
{
    public static async Task RegisterAsync(ManagementApi api, string user, TenantContext database, string name, IReadOnlyList<string> replicas,
        CancellationToken cancellationToken = default)
    {
        await RegisterProjectAsync(api, user, database.OrganizationId, database.ProjectId!, cancellationToken);
        var directory = api.Server.Services.GetRequiredService<ITenantDirectory>();
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            var existing = await directory.ResolveProjectDatabaseAsync(database.ProjectId!, name, linked.Token);
            if (existing is Unavailable)
            {
                return false;
            }
            if (existing is ResolvedProjectDatabase registered && registered.Database.OrganizationId == database.OrganizationId
                && registered.Database.DatabaseId == database.DatabaseId)
            {
                var region = api.Server.Services.GetRequiredService<TerraBase.Configuration.TerraBaseOptions>().Region;
                if (!registered.Placement.Includes(region))
                {
                    return true;
                }

                return await directory.EnsureLocalDatabaseAsync(registered.Database, linked.Token) switch
                {
                    Done => true,
                    Unavailable => false,
                    NotFound => throw new InvalidOperationException("The fixture database registration disappeared"),
                };
            }

            var result = await directory.AdoptDatabaseAsync(database, name,
                new DatabasePlacement(api.Server.Services.GetRequiredService<TerraBase.Configuration.TerraBaseOptions>().Region, replicas, 1), linked.Token);
            return result switch
            {
                Done => true,
                Unavailable => false,
                NotFound => throw new InvalidOperationException("The fixture project is missing"),
                Conflict error => throw new InvalidOperationException(error.ToString()),
            };
        }, TimeSpan.FromMinutes(2));
    }

    public static async Task RegisterProjectAsync(ManagementApi api, string user, string organizationId, string projectId,
        CancellationToken cancellationToken = default)
    {
        using var owner = api.As(user);
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            using var response = await owner.GetAsync(ManagementApi.Uri("/v1/management/organizations"), linked.Token);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return false;
            }

            response.EnsureSuccessStatusCode();
            return true;
        }, TimeSpan.FromMinutes(2));

        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(cancellationToken);
        await using var transaction = await store.BeginTransactionAsync(write: true, cancellationToken);
        if (await transaction.GetAsync("organizations", organizationId, cancellationToken) is null)
        {
            var organization = new Organization(organizationId, organizationId, user, DateTimeOffset.UnixEpoch);
            await transaction.PutAsync("organizations", organizationId,
                EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(organization, TerraBaseJsonContext.Default.Organization), organizationId, nested: true), cancellationToken);
            await transaction.PutAsync($"users/{user}/organizations", organizationId, JsonSerializer.SerializeToUtf8Bytes(organizationId), cancellationToken);
            await transaction.PutAsync($"users/{user}/organization_names", organizationId.ToLowerInvariant(), JsonSerializer.SerializeToUtf8Bytes(organizationId), cancellationToken);
        }

        if (await transaction.GetAsync("project_organizations", projectId, cancellationToken) is null)
        {
            var project = new Project(projectId, organizationId, projectId, DateTimeOffset.UnixEpoch);
            await transaction.PutAsync($"organizations/{organizationId}/projects", projectId,
                EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(project, TerraBaseJsonContext.Default.Project), projectId, nested: true), cancellationToken);
            await transaction.PutAsync("project_organizations", projectId, JsonSerializer.SerializeToUtf8Bytes(organizationId), cancellationToken);
            await transaction.PutAsync($"organizations/{organizationId}/project_names", projectId.ToLowerInvariant(), JsonSerializer.SerializeToUtf8Bytes(projectId), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        api.Issuer.RegisterProject(projectId, organizationId);
    }
}
