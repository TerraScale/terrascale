using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class DeleteDatabaseEndpoint(DatabaseAccess access, ManagementService management) : Endpoint<DatabaseInput, DeleteDatabaseResponse>
{
    public override void Configure()
    {
        Delete(ManagementRoute.Database);
        Name("deleteDatabase");
        Summary("Deletes the database and crypto-shreds its data");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<DeleteDatabaseResponse> HandleAsync(DatabaseInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await DatabaseDeletion.DeleteAsync(HttpContext.Caller, access, management, request, cancellationToken);
    }
}

internal static class DatabaseDeletion
{
    public static async ValueTask<DeleteDatabaseResponse> DeleteAsync(
        Caller caller, DatabaseAccess access, ManagementService management, DatabaseInput request, CancellationToken cancellationToken) =>
        await access.ManagedAsync(caller, request.Project, request.Database, Scopes.DatabasesWrite, cancellationToken) switch
        {
            ResolvedDatabase resolved when request.ExpectedId is { } expected && !string.Equals(expected, resolved.Database.Id.ToString(), StringComparison.Ordinal) =>
                new PreconditionFailed("the database's id is not the expected one", "database_id_mismatch"),
            ResolvedDatabase resolved => await management.DeleteDatabaseAsync(resolved.Database, cancellationToken) switch
            {
                Done done => done,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
}
