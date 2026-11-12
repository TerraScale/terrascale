using System.Net;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed record ProjectSession(string Id, SessionRecord Session, string Email);

public sealed class TenantDirectory(AuthData data, Users users, Sessions sessions, PlatformDirectory platform, TimeProvider clock, ProjectAuthSettings settings)
{
    internal const string ClientIssuanceCaller = "ts-auth:client-issuance-caller";

    public async Task CheckAccessAsync(string userId, string projectId, string minimum, CancellationToken cancellationToken)
    {
        var project = await platform.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        PlatformDirectory.RequireRole(await platform.ProjectAccessAsync(userId, project.Value.OrganizationId, projectId, cancellationToken).ConfigureAwait(false), minimum);
    }

    public async Task CheckKeyAccessAsync(ApiKeyRecord key, string projectId, string scope, IPAddress? address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var project = await platform.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        if (key.ProjectId != projectId || key.OrganizationId != project.Value.OrganizationId || key.Database is not null
            || await platform.ClosureAsync("organization", key.OrganizationId, cancellationToken).ConfigureAwait(false) is not null
            || await platform.ClosureAsync("project", projectId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw PlatformDirectory.NotFound();
        }

        if (!key.Scopes.Contains(scope, StringComparer.Ordinal))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "insufficient_scope", $"This operation requires {scope}.");
        }

        if (!Allows(key.AllowedCidrs, address) || !Allows(project.Value.AllowedCidrs, address))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "ip_not_allowed", "The request address is outside the key or project's IP allowlist.");
        }
    }

    public Task<IResult> AuditKeyWriteAsync(ApiKeyRecord key, string operation, string target, Func<CancellationToken, Task<IResult>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return data.AuditWritesAsync(platform.Audit(key.OrganizationId, key.Id, operation + ".succeeded", key.ProjectId, target), write, cancellationToken);
    }

    public async Task AuditKeyFailureAsync(ApiKeyRecord key, string operation, string target, int status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!await data.SaveAsync(platform.Audit(key.OrganizationId, key.Id, operation + ".failed", key.ProjectId, $"{target} status={status}"), cancellationToken).ConfigureAwait(false))
        {
            throw ApiException.Unavailable();
        }
    }

    private static bool Allows(IReadOnlyList<string>? cidrs, IPAddress? address) =>
        cidrs is not { Count: > 0 }
        || address is not null && cidrs.Any(cidr => IPNetwork.Parse(cidr).Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));

    public async Task<Page<UserRecord>> UsersAsync(string projectId, string? query, int limit, string? after, CancellationToken cancellationToken)
    {
        var from = PlatformDirectory.Cursor(after);
        var needle = string.IsNullOrWhiteSpace(query) ? null : query.Trim().ToLowerInvariant();
        var end = needle + "\uffff";
        var rows = await data.ReadAsync((db, token) => db.Users
            .Where(user => user.InstanceId == projectId && string.Compare(user.Id, from) > 0
                && (needle == null || string.Compare(user.Email, needle) >= 0 && string.Compare(user.Email, end) < 0))
            .OrderBy(user => user.Id).Take(limit + 1)
            .Select(user => new { User = user, Links = db.ProviderLinks.Where(link => link.UserId == user.Id).OrderBy(link => link.Provider).ThenBy(link => link.Subject).ToList() })
            .ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return PlatformDirectory.Paged([.. rows.Select(row => row.User with { LinkedProviders = [.. row.Links.Select(link => link.Identity)] })], limit, user => user.Id);
    }

    public async Task<UserRecord> UserAsync(string projectId, string userId, CancellationToken cancellationToken) =>
        await users.GetAsync(projectId, userId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();

    public async Task<UserRecord> SetDisabledAsync(string projectId, string userId, bool disabled, CancellationToken cancellationToken) =>
        await users.SetDisabledAsync(projectId, userId, disabled, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();

    public async Task<Page<ProjectSession>> SessionsAsync(string projectId, string? userId, int limit, string? after, CancellationToken cancellationToken)
    {
        var configuration = (await settings.GetAsync(projectId, cancellationToken).ConfigureAwait(false)).Configuration ?? ConfigurationBody.Default;
        var lifetime = TimeSpan.FromSeconds(configuration.SessionLifetimeSeconds);
        var now = clock.GetUtcNow();
        var from = PlatformDirectory.Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.Sessions
            .Where(session => session.InstanceId == projectId && !session.Revoked && session.ExpiresAt > now && (userId == null || session.UserId == userId)
                && string.Compare(session.Id, from) > 0)
            .OrderBy(session => session.Id).Take(limit + 1)
            .Join(db.Users, session => session.UserId, user => user.Id, (session, user) => new { Session = session, User = user })
            .ToListAsync(token), cancellationToken).ConfigureAwait(false);
        var next = rows.Count > limit ? rows[limit - 1].Session.Id : null;
        List<ProjectSession> items = [];
        foreach (var row in rows.Take(limit))
        {
            var policyExpiry = row.Session.CreatedAt + lifetime;
            var session = row.Session with { ExpiresAt = policyExpiry < row.Session.ExpiresAt ? policyExpiry : row.Session.ExpiresAt };
            if (session.ExpiresAt > clock.GetUtcNow() && row.User is { Disabled: false } user && user.AuthEpoch == session.AuthEpoch && user.AuthStamp == session.AuthStamp
                && user.SessionStamp == session.SessionStamp
                && (configuration.InactivityTimeoutMinutes is not { } idle || (session.LastActivityAt ?? session.CreatedAt).AddMinutes(idle) > clock.GetUtcNow()))
            {
                items.Add(new ProjectSession(session.Id, session, user.Email));
            }
        }

        return new(items, next);
    }

    public async Task<bool> RevokeSessionAsync(string projectId, string id, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => db.Sessions.Where(session => session.InstanceId == projectId && session.Id == id).Select(session => session.TokenHash)
            .SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false) is { } tokenHash
        && await sessions.RevokeStoredAsync(projectId, tokenHash, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<OidcApplication>> ClientsAsync(string projectId, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => db.OidcApplications.Where(client => client.InstanceId == projectId).OrderBy(client => client.Id).ToListAsync(token), cancellationToken)
            .ConfigureAwait(false);

    public async Task<(OidcApplication Client, string? Secret)> CreateClientAsync(HttpContext context, UserRecord caller, string projectId, string name, string type, IReadOnlyList<Uri> redirectUris, IOpenIddictApplicationManager manager, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.StepUpBindings.Count == 0)
        {
            throw StepUpProofs.Required();
        }
        var secret = type == ClientTypes.Confidential ? Tokens.RandomToken(48) : null;
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = AuthIds.New(),
            ClientSecret = secret,
            ClientType = type,
            ApplicationType = ApplicationTypes.Web,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = name,
            Permissions =
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession, Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.OpenId, Permissions.Scopes.Email, Permissions.Scopes.Profile, Permissions.Prefixes.Scope + Scopes.OfflineAccess,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        descriptor.RedirectUris.UnionWith(redirectUris);
        descriptor.PostLogoutRedirectUris.UnionWith(redirectUris);
        var previous = context.Items[Tenancy.ItemKey];
        context.Items[Tenancy.ItemKey] = projectId;
        context.Items[ClientIssuanceCaller] = caller;
        try
        {
            return ((OidcApplication)await manager.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false), secret);
        }
        catch (OpenIddictExceptions.ValidationException error)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_request", error.Results.FirstOrDefault()?.ErrorMessage ?? "The client is invalid.");
        }
        finally
        {
            context.Items[Tenancy.ItemKey] = previous;
            context.Items.Remove(ClientIssuanceCaller);
        }
    }

    public async Task AuditClientRotationAsync(string userId, string projectId, string clientId, CancellationToken cancellationToken)
    {
        var project = await platform.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        await data.SaveAsync(platform.Audit(project.Value.OrganizationId, userId, "oauth_client.rotated", clientId), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteClientAsync(string projectId, string clientId, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => db.OidcApplications.SingleOrDefaultAsync(client => client.InstanceId == projectId && client.Id == clientId, token), cancellationToken)
            .ConfigureAwait(false) is { } client
        && await data.SaveAsync(Change.Delete(client), cancellationToken).ConfigureAwait(false);
}
