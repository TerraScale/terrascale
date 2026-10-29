using System.Diagnostics;
using System.Text.Json;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    // Leaderless (docs/terrabase/database-rename.md): the home region accepts a free name in one catalog transaction. Two
    // regions can accept one name at once; convergence keeps the smaller ULID on it and moves the other (KeepLoserAsync).
    public ValueTask<SetDatabaseRegionsResult> RenameDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken) =>
        WriteAsync<SetDatabaseRegionsResult>(async transaction =>
        {
            var project = await AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (project is not TenantContext tenant)
            {
                return project switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    TenantContext => throw new UnreachableException(),
                };
            }

            if (await GetDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is not { } database
                || await DatabasePurge.IsDeletedAsync(transaction, database.Id, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NotFound;
            }

            // The home region is the one writer of a database's label, so renames of one database never race each other.
            if (database.Home(options.Region) != options.Region)
            {
                return ManagementErrors.WrongRegion;
            }

            if (database.Name == name && database.ConflictOf is null)
            {
                return database;
            }

            // A holder whose record has not replicated here yet still holds its name.
            if (await NameHolderAsync(transaction, projectId, name, cancellationToken).ConfigureAwait(false) is { } holder && holder != database.Id)
            {
                return ManagementErrors.NameTaken;
            }

            // Only entries still pointing at this database are released: after a race a name can be another database's.
            foreach (var released in (await StaleNamesAsync(transaction, database, cancellationToken).ConfigureAwait(false)).Prepend(database.Name))
            {
                if (released != name && await GetAsync(transaction, DatabaseNames(projectId), released, Id, cancellationToken).ConfigureAwait(false) == database.Id)
                {
                    _ = await transaction.DeleteAsync(DatabaseNames(projectId), released, cancellationToken).ConfigureAwait(false);
                }
            }

            await PutAsync(transaction, DatabaseNames(projectId), name, database.Id, Id, cancellationToken).ConfigureAwait(false);

            // The customer chose this name, so a conflict it had lost is settled. Only the label fields are written: a full row
            // would renew the placement fields over a newer placement elsewhere.
            var renamed = database with { Name = name, PreviousName = null, ConflictOf = null };
            FieldOperation[] fields =
            [
                new(FieldOperationKind.Set, "name", JsonSerializer.SerializeToElement(name, Id)),
                new(FieldOperationKind.Unset, "previousName"),
                new(FieldOperationKind.Unset, "conflictOf"),
            ];
            var json = EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(renamed, TerraBaseJsonContext.Default.ManagedDatabase), database.Id, nested: true);
            _ = await transaction.PatchAsync(Databases(projectId), database.Id, json, fields, cancellationToken).ConfigureAwait(false);

            return renamed;
        }, static error => error, cancellationToken);
}
