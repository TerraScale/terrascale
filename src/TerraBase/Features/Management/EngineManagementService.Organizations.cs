using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Platform;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{

    public ValueTask<CreateOrganizationResult> CreateOrganizationAsync(string userId, string name, CancellationToken cancellationToken) =>
        CreateOrganizationAsync(userId, NewId(), name, cancellationToken);

    private async ValueTask<CreateOrganizationResult> CreateOrganizationAsync(string userId, string id, string name, CancellationToken cancellationToken)
    {
        var organization = new Organization(id, name, userId, time.GetUtcNow());
        return await WriteAsync<CreateOrganizationResult>(async transaction =>
        {
            if (await CatalogTables.OrganizationAsync(transaction, id, cancellationToken).ConfigureAwait(false) is { } known)
            {
                return known.Name == name && known.CreatedBy == userId ? known : ManagementErrors.IdTaken;
            }

            if (await CatalogTables.RetiredAsync(transaction, id, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.IdTaken;
            }

            if (await CatalogTables.OrganizationNamedAsync(transaction, userId, Key(name), cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementErrors.NameTaken;
            }

            _ = await CatalogTables.PutOrganizationAsync(transaction, organization, cancellationToken).ConfigureAwait(false);
            return organization;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ListOrganizationsResult> ListOrganizationsAsync(string userId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ListOrganizationsResult>(async transaction =>
            await CatalogTables.OrganizationsCreatedByAsync(transaction, userId, after, limit, cancellationToken).ConfigureAwait(false),
            static error => error, cancellationToken);

    public ValueTask<GetOrganizationResult> GetOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        ReadAsync<GetOrganizationResult>(async transaction =>
            await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is { } organization
                ? organization
                : ManagementErrors.NotFound,
            static error => error, cancellationToken);


    public async ValueTask<DeleteOrganizationResult> DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        await WriteAsync<DeleteOrganizationResult>(async transaction =>
        {
            var access = await AuthorizeAsync(transaction, userId, organizationId, allowProject: false, cancellationToken).ConfigureAwait(false);
            if (access is not Organization)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    Organization => throw new UnreachableException(),
                };
            }

            var organization = await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("an organization index names a missing organization");

            if (await CatalogTables.HasProjectsAsync(transaction, organization.Id, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NotEmpty;
            }

            await CatalogTables.DeleteOrganizationAsync(transaction, organization.Id, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);
}
