using System.Diagnostics;
using System.Collections.Frozen;
using System.Security.Claims;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Primitives;

namespace TerraBase.Features.UserAuth;

internal static class ProjectGrants
{
    internal static async ValueTask<ProjectGrant> GrantAsync(HttpContext context, ClaimsIdentity jwt, string project, bool bucketCreation)
    {
        var actor = jwt.FindFirst(ClaimTypes.NameIdentifier)!.Value;
        var catalog = context.RequestServices.GetRequiredService<IProjectCatalog>();
        var resolved = await catalog.ResolveProjectAsync(project, actor,
            createIn: bucketCreation ? jwt.FindFirst("org_id")?.Value : null, context.RequestAborted).ConfigureAwait(false);
        if (resolved is not string organization)
        {
            return resolved switch
            {
                NotFound => new NotFound("the database does not exist", "unknown_database"),
                Unavailable error => error,
                string => throw new UnreachableException(),
            };
        }

        var restricted = jwt.FindFirst("database")?.Value;
        var restrictedId = restricted is not null && EntityIds.IsValid(restricted) ? restricted : null;
        if (restricted is not null)
        {
            var restriction = await context.RequestServices.GetRequiredService<IManagementService>()
                .FindDatabaseRecordAsync(organization, project, restricted, context.RequestAborted).ConfigureAwait(false);
            switch (restriction)
            {
                case Unavailable unavailable:
                    return unavailable;
                case ManagedDatabase record:
                    restrictedId = record.Id;
                    break;
            }
        }

        return new ProjectAccess(actor, project, organization, restricted, restrictedId)
        {
            IsEndUser = jwt.HasClaim("token_kind", "end_user"),
            Roles = jwt.FindAll("roles").Select(claim => claim.Value).ToFrozenSet(StringComparer.Ordinal),
            Tables = jwt.FindAll("tables").Select(claim => claim.Value).ToFrozenSet(StringComparer.Ordinal),
        };
    }
}
