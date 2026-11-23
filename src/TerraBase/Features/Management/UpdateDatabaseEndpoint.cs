using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class UpdateDatabaseEndpoint(DatabaseAccess access, ManagementService management, TerraBaseOptions options, TimeProvider time)
    : Endpoint<DatabaseInput, UpdateDatabaseResponse>
{
    public override void Configure()
    {
        Patch(ManagementRoute.Database);
        Name("updateDatabase");
        Summary("Renames a database or changes its regions, consistency or TTL");
        Description("regions (or replicaRegions) replaces the set of equal active-active regions; region adds one region; allRegions places it everywhere.");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<UpdateDatabaseResponse> HandleAsync(DatabaseInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await DatabaseUpdates.UpdateAsync(HttpContext.Caller, access, management, options, time, request, cancellationToken);
    }
}

internal static class DatabaseUpdates
{
    public static async ValueTask<UpdateDatabaseResponse> UpdateAsync(
        Caller caller, DatabaseAccess access, ManagementService management, TerraBaseOptions options, TimeProvider time, DatabaseInput request,
        CancellationToken cancellationToken) =>
        await access.ManagedAsync(caller, request.Project, request.Database, Scopes.DatabasesWrite, cancellationToken) switch
        {
            ResolvedDatabase resolved => await management.UpdateDatabaseAsync(resolved.Database, request, cancellationToken) switch
            {
                DatabaseRecord updated => DatabaseBody.From(updated, options.Region, time.GetUtcNow()),
                Validation invalid => invalid,
                Conflict conflict => conflict,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };

    public static async ValueTask<UpdateDatabaseResponse> TtlAsync(
        Caller caller, DatabaseAccess access, ManagementService management, TerraBaseOptions options, TimeProvider time, DatabaseInput request, long? ttlSeconds,
        CancellationToken cancellationToken) =>
        await access.ManagedAsync(caller, request.Project, request.Database, Scopes.DatabasesWrite, cancellationToken) switch
        {
            ResolvedDatabase resolved => await management.SetTtlAsync(resolved.Database, ttlSeconds, cancellationToken) switch
            {
                DatabaseRecord updated => DatabaseBody.From(updated, options.Region, time.GetUtcNow()),
                Validation invalid => invalid,
                Conflict conflict => conflict,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
}
