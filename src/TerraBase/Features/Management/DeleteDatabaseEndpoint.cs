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
    private static readonly CallerForbidden AgentForbidden =
        new("an agent acting for a user cannot delete a database; the user must delete it", "agent_action_forbidden");

    public static async ValueTask<DeleteDatabaseResponse> DeleteAsync(
        Caller caller, DatabaseAccess access, ManagementService management, DatabaseInput request, CancellationToken cancellationToken)
    {
        if (caller is UserCaller { Delegated: true } or ProjectCaller { Delegated: true })
        {
            return AgentForbidden;
        }

        var outcome = await access.ManagedAsync(caller, request.Project, request.Database, Scopes.DatabasesWrite, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved when request.ExpectedId is { } expected && !string.Equals(expected, resolved.Database.Id.ToString(), StringComparison.Ordinal) =>
                new DatabaseIdMismatch(),
            ResolvedDatabase resolved => await management.DeleteDatabaseAsync(resolved.Database, cancellationToken) switch
            {
                Completed done => done,
                StateConflict conflict => conflict,
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
