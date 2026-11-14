using System.Net;
using Microsoft.AspNetCore.Authorization;
using TerraBase.Errors;
using TerraBase.Engine;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Sql;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Regions;

internal readonly union ForwardingAdmission(TenantContext, Done, NotFound, Forbidden, Unavailable);

internal static class ForwardingAuthorization
{
    internal sealed class Refused;
    internal sealed class ExecutorResolves;
    private sealed class Authorized;
    private static readonly string[] SqlScopes = [ApiKeyScopes.SqlRead];

    internal static async ValueTask<ForwardingAdmission> AuthorizeAsync(IServiceProvider services, ApiKey key,
        string project, string? database, IReadOnlyList<string> scopes, IReadOnlyList<IPNetwork[]> allowlists,
        IPAddress? client, CancellationToken cancellationToken)
    {
        if (key.ProjectId is not null && key.ProjectId != project)
        {
            return DatabaseRoute.UnknownDatabase;
        }
        if (scopes.Any(scope => !key.Scopes.Contains(scope)))
        {
            return new Forbidden("SQL needs the sql:read scope", "forbidden_scope");
        }
        if (!IpAllowlists.Admits(allowlists, client))
        {
            return new Forbidden(IpAllowlists.Refusal(client), "ip_not_allowed");
        }

        switch (await services.GetRequiredService<IProjectCatalog>()
            .ProjectOrganizationAsync(project, cancellationToken).ConfigureAwait(false))
        {
            case string organization when organization != key.OrganizationId:
                return DatabaseRoute.UnknownDatabase;
            case NotFound when key.ProjectId != project:
                return DatabaseRoute.UnknownDatabase;
            case Unavailable unavailable:
                return unavailable;
        }

        var tenants = services.GetRequiredService<ITenantDirectory>();
        if (key.DatabaseId is { } restriction && !EntityIds.IsValid(restriction))
        {
            switch (await tenants.FindDatabaseAsync(key.OrganizationId, project, restriction, cancellationToken).ConfigureAwait(false))
            {
                case TenantContext restricted:
                    key = key with { DatabaseId = restricted.DatabaseId };
                    break;
                case Unavailable unavailable:
                    return unavailable;
                case NotFound:
                    return DatabaseRoute.UnknownDatabase;
            }
        }
        var canonical = database;
        if (database is not null)
        {
            if (!EntityIds.IsValid(database) && !DatabaseName.TryParse(database, out _))
            {
                return DatabaseRoute.UnknownDatabase;
            }
            switch (await tenants.FindDatabaseAsync(key.OrganizationId, project, database, cancellationToken).ConfigureAwait(false))
            {
                case TenantContext found:
                    return key.Reaches(found) ? found : DatabaseRoute.UnknownDatabase;
                case Unavailable unavailable:
                    return unavailable;
            }
        }
        if (key.DatabaseId is not null && (canonical is null || !EntityIds.IsValid(key.DatabaseId)
            || key.DatabaseId != canonical))
        {
            return DatabaseRoute.UnknownDatabase;
        }
        return new Done();
    }

    internal static async ValueTask<bool> CheckAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Features.Get<Authorized>() is not null || context.Request.RouteValues["project"] is not string project)
        {
            return true;
        }
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }
        var socket = context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "connectSql";
        if (socket && !(await context.RequestServices.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(context.User, context, ApiKeyScopes.SqlRead).ConfigureAwait(false)).Succeeded)
        {
            return await RefuseAsync(new Forbidden("SQL needs the sql:read scope", "forbidden_scope")).ConfigureAwait(false);
        }

        var key = context.Features.Get<ApiKey>();
        var token = context.Features.Get<ProjectToken>();
        if (key is null && context.Features.Get<ProjectAccess>() is null
            && UserAuthExtensions.Identity(context.User) is { } identity)
        {
            switch (await ProjectGrants.GrantAsync(context, identity, project, bucketCreation: false).ConfigureAwait(false))
            {
                case ProjectAccess access:
                    context.Features.Set(access);
                    break;
                case NotFound missing:
                    return await RefuseAsync(missing).ConfigureAwait(false);
                case Unavailable unavailable:
                    return await RefuseAsync(unavailable).ConfigureAwait(false);
            }
        }
        if (context.Features.Get<ProjectAccess>() is { } grant)
        {
            key = new ApiKey(grant.Actor, "project grant", context.User.FindAll(ApiKeyAuthenticationHandler.ScopeClaim)
                .Select(claim => claim.Value).ToArray(), grant.OrganizationId, grant.ProjectId,
                grant.DatabaseName is null ? null : grant.DatabaseId ?? grant.DatabaseName);
        }
        if (key is null)
        {
            return false;
        }
        var result = await AuthorizeAsync(context.RequestServices, key, project, context.Request.RouteValues["database"] as string,
            socket ? SqlScopes : [], token?.Allowlists ?? [], IpAllowlists.ClientOf(context), cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case TenantContext tenant:
                UsageRequestMetering.Attribute(context, tenant);
                context.Features.Set(new Authorized());
                return true;
            case Done:
                context.Features.Set(new ExecutorResolves());
                context.Features.Set(new Authorized());
                return true;
        }
        return result switch
        {
            NotFound missing => await RefuseAsync(missing).ConfigureAwait(false),
            Forbidden forbidden => await RefuseAsync(forbidden).ConfigureAwait(false),
            Unavailable unavailable => await RefuseAsync(unavailable).ConfigureAwait(false),
            TenantContext or Done => throw new InvalidOperationException("authorization was already handled"),
        };

        async ValueTask<bool> RefuseAsync(ForwardingAdmission failure)
        {
            var (result, code, message) = failure switch
            {
                NotFound error => (ErrorResponse.From(error), error.Code, error.Message ?? "the database does not exist"),
                Forbidden error => (ErrorResponse.From(error), error.Code, error.Message),
                Unavailable error => (ErrorResponse.From(error), error.Code, error.Message),
                TenantContext or Done => throw new InvalidOperationException("an authorized request cannot be refused"),
            };
            context.Features.Set(new Refused());
            if (context.Features.Get<HomeSocketRelay.BrowserConnection>() is { } browser)
            {
                await HomeSocketRelay.RefuseAsync(context, browser.Socket, browser.Id,
                    failure is NotFound ? SqlErrors.Unauthorized : code,
                    failure is NotFound ? "the database does not exist or the credential does not reach it" : message, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await result.ExecuteAsync(context).ConfigureAwait(false);
            }
            return false;
        }
    }
}
