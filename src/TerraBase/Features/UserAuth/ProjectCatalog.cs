using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Primitives;

using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

internal static class ProjectCatalog
{
    internal static async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var endpoint = context.GetEndpoint();
        var jwt = UserAuthExtensions.Identity(context.User);
        if (context.Request.RouteValues["project"] is not string project || context.Request.RouteValues.ContainsKey("organization"))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (endpoint?.Metadata.GetMetadata<ManagementUserRoute>() is not null
            && jwt?.FindFirst("project_id") is null
            && (jwt is not null || context.User.IsOperator()))
        {
            await AdmitManagementUserAsync(context, next, project, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (jwt is not { } identity)
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (endpoint?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
                is CreateManagedDatabaseEndpoint.OperationName or ListManagedDatabasesEndpoint.ListOperationName)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var bucketCreation = endpoint?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "createFileBucket"
            && !identity.HasClaim("token_kind", "end_user") && identity.FindFirst("database") is null;
        var granted = await ProjectGrants.GrantAsync(context, identity, project, bucketCreation).ConfigureAwait(false);
        if (granted is not ProjectAccess grant)
        {
            await (granted switch
            {
                NotFound refused => ErrorResponse.From(refused),
                Unavailable unavailable => ErrorResponse.From(unavailable),
                ProjectAccess => throw new UnreachableException(),
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        context.Features.Set(grant);
        var management = context.RequestServices.GetRequiredService<IManagementService>();
        var (organization, restricted, restrictedId) = (grant.OrganizationId, grant.DatabaseName, grant.DatabaseId);

        if (endpoint?.Metadata.GetMetadata<ProjectManagementRoute>() is not null)
        {
            context.Request.RouteValues["organization"] = organization;
        }

        if (endpoint?.Metadata.GetMetadata<ProjectManagementRoute>() is not null || restricted is not null)
        {
            if (await ResolveDatabaseAsync(context, management, organization, project) is not DatabaseResolution.Resolved)
            {
                return;
            }

            if (restricted is not null && restrictedId != context.Request.RouteValues["database"] as string)
            {
                await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
                return;
            }
        }

        await ProjectAccess.RunAsync(grant, () => next()).ConfigureAwait(false);
    }

    private static async Task AdmitManagementUserAsync(HttpContext context, Func<Task> next, string project, CancellationToken cancellationToken)
    {
        var actor = ManagementRoute.UserId(context);
        var resolved = await context.RequestServices.GetRequiredService<IProjectCatalog>()
            .ResolveProjectAsync(project, actor, createIn: null, cancellationToken).ConfigureAwait(false);
        if (resolved is not string owner)
        {
            await (resolved switch
            {
                NotFound => UnknownProject(context),
                Unavailable unavailable => ErrorResponse.From(unavailable),
                string => throw new UnreachableException(),
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (await context.RequestServices.GetRequiredService<IManagementService>()
                .GetProjectAsync(actor, owner, project, cancellationToken).ConfigureAwait(false) is not Project)
        {
            await UnknownProject(context).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        context.Request.RouteValues["organization"] = owner;
        if (await ResolveDatabaseAsync(
                context, context.RequestServices.GetRequiredService<IManagementService>(), owner, project) is not DatabaseResolution.Resolved)
        {
            return;
        }

        await next().ConfigureAwait(false);
    }

    private static IResult UnknownProject(HttpContext context) =>
        ErrorResponse.From(new NotFound("the database does not exist", "unknown_database"));

    private enum DatabaseResolution
    {
        Resolved,
        Refused,
    }

    private static async ValueTask<DatabaseResolution> ResolveDatabaseAsync(
        HttpContext context, IManagementService management, string organization, string project)
    {
        if (context.Request.RouteValues["database"] is not string name)
        {
            return DatabaseResolution.Resolved;
        }

        if (!EntityIds.IsValid(name) && !DatabaseName.TryParse(name, out _))
        {
            await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
            return DatabaseResolution.Refused;
        }

        var found = await management.FindDatabaseRecordAsync(organization, project, name, context.RequestAborted).ConfigureAwait(false);
        if (found is not ManagedDatabase record)
        {
            await (found switch
            {
                NotFound => ErrorResponse.From(new NotFound("the database does not exist", "unknown_database")),
                Unavailable error => ErrorResponse.From(error),
                ManagedDatabase => throw new UnreachableException(),
            }).ExecuteAsync(context).ConfigureAwait(false);
            return DatabaseResolution.Refused;
        }

        context.Request.RouteValues["databaseName"] = record.Name;
        context.Request.RouteValues["database"] = record.Id;
        return DatabaseResolution.Resolved;
    }
}
