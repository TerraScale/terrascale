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
        var managementRoute = context.GetEndpoint()?.Metadata.GetMetadata<ProjectManagementRoute>() is not null;
        if (context.Request.RouteValues["project"] is not string project || context.Request.RouteValues.ContainsKey("organization")
            || UserAuthExtensions.Identity(context.User) is not { } jwt)
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
                is CreateManagedDatabaseEndpoint.OperationName or ListManagedDatabasesEndpoint.ListOperationName)
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (jwt.FindFirst("project_id") is null)
        {
            if (managementRoute)
            {
                var resolved = await context.RequestServices.GetRequiredService<IProjectCatalog>()
                    .ResolveProjectAsync(project, ManagementRoute.UserId(context), createIn: null, context.RequestAborted).ConfigureAwait(false);
                if (resolved is not string owner)
                {
                    await (resolved switch
                    {
                        NotFound => ErrorResponse.From(new NotFound("the database does not exist", "unknown_database")),
                        Unavailable unavailable => ErrorResponse.From(unavailable),
                        string => throw new UnreachableException(),
                    }).ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }

                context.Request.RouteValues["organization"] = owner;
            }

            await next().ConfigureAwait(false);
            return;
        }

        var bucketCreation = context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "createFileBucket"
            && !jwt.HasClaim("token_kind", "end_user") && jwt.FindFirst("database") is null;
        var granted = await ProjectGrants.GrantAsync(context, jwt, project, bucketCreation).ConfigureAwait(false);
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

        if (managementRoute)
        {
            context.Request.RouteValues["organization"] = organization;
        }

        if (managementRoute || restricted is not null)
        {
            if (context.Request.RouteValues["database"] is string name)
            {
                if (!EntityIds.IsValid(name) && !DatabaseName.TryParse(name, out _))
                {
                    await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
                    return;
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
                    return;
                }

                if (restricted is not null && restrictedId != record.Id)
                {
                    await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
                    return;
                }

                if (managementRoute)
                {
                    context.Request.RouteValues["databaseName"] = record.Name;
                }
                context.Request.RouteValues["database"] = record.Id;
            }
        }

        await ProjectAccess.RunAsync(grant, () => next()).ConfigureAwait(false);
    }
}
