using System.Net;
using Dapper;
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

public sealed record NewClient(string Name, string Type, IReadOnlyList<Uri> RedirectUris, MachineClientPolicy? Policy, IReadOnlyList<string> AllowedOrigins, bool AllowAnonymous);

public sealed class TenantDirectory(AuthData data, Users users, Sessions sessions, PlatformDirectory platform, TimeProvider clock, ProjectAuthSettings settings)
{
    internal const string ClientIssuanceCaller = "ts-auth:client-issuance-caller";

    public async Task CheckAccessAsync(string userId, string projectId, string minimum, CancellationToken cancellationToken)
    {
        var project = await platform.ProjectAsync(projectId, cancellationToken) ?? throw PlatformDirectory.NotFound();
        PlatformDirectory.RequireRole(await platform.ProjectAccessAsync(userId, project.Value.OrganizationId, projectId, cancellationToken), minimum);
    }

    public async Task CheckKeyAccessAsync(ApiKeyRecord key, string projectId, string scope, IPAddress? address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var project = await platform.ProjectAsync(projectId, cancellationToken) ?? throw PlatformDirectory.NotFound();
        if (key.ProjectId != projectId || key.OrganizationId != project.Value.OrganizationId || key.Database is not null
            || await platform.ClosureAsync("organization", key.OrganizationId, cancellationToken) is not null
            || await platform.ClosureAsync("project", projectId, cancellationToken) is not null)
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
        if (!await data.SaveAsync(platform.Audit(key.OrganizationId, key.Id, operation + ".failed", key.ProjectId, $"{target} status={status}"), cancellationToken))
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
        var take = limit + 1;
        var users = (await data.ReadUsersAsync(connection => connection.QueryAsync<UserRecord>(
            """
            SELECT * FROM users WHERE instance_id = @projectId AND id > @from AND (@needle IS NULL OR email >= @needle AND email < @end)
            ORDER BY id LIMIT @take
            """, new { projectId, from, needle, end, take }), cancellationToken)).AsList();
        var ids = users.Select(user => user.Id).ToList();
        var links = (await data.ReadAsync(connection => connection.QueryAsync<ProviderLinkRecord>(
            "SELECT * FROM provider_links WHERE user_id IN @ids ORDER BY provider, subject", new { ids }), cancellationToken)).ToLookup(link => link.UserId, StringComparer.Ordinal);
        return PlatformDirectory.Paged([.. users.Select(user => user with { LinkedProviders = [.. links[user.Id].Select(link => link.Identity)] })], limit, user => user.Id);
    }

    public async Task<UserRecord> UserAsync(string projectId, string userId, CancellationToken cancellationToken) =>
        await users.GetAsync(projectId, userId, cancellationToken) ?? throw PlatformDirectory.NotFound();

    public async Task<UserRecord> SetDisabledAsync(string projectId, string userId, bool disabled, CancellationToken cancellationToken) =>
        await users.SetDisabledAsync(projectId, userId, disabled, cancellationToken) ?? throw PlatformDirectory.NotFound();

    public async Task<Page<ProjectSession>> SessionsAsync(string projectId, string? userId, int limit, string? after, CancellationToken cancellationToken)
    {
        var configuration = (await settings.GetAsync(projectId, cancellationToken)).Configuration ?? ConfigurationBody.Default;
        var lifetime = TimeSpan.FromSeconds(configuration.SessionLifetimeSeconds);
        var now = clock.GetUtcNow();
        var from = PlatformDirectory.Cursor(after);
        var take = limit + 1;
        var found = (await data.ReadAsync(connection => connection.QueryAsync<SessionRecord>(
            """
            SELECT * FROM sessions WHERE instance_id = @projectId AND revoked = 0 AND expires_at > @now AND (@userId IS NULL OR user_id = @userId) AND id > @from
            ORDER BY id LIMIT @take
            """, new { projectId, now, userId, from, take }), cancellationToken)).AsList();
        var owners = found.Select(session => session.UserId).Distinct(StringComparer.Ordinal).ToList();
        var byId = (await data.ReadUsersAsync(connection => connection.QueryAsync<UserRecord>("SELECT * FROM users WHERE id IN @owners", new { owners }), cancellationToken))
            .ToDictionary(user => user.Id, StringComparer.Ordinal);
        var rows = found.Where(session => byId.ContainsKey(session.UserId)).Select(session => (Session: session, User: byId[session.UserId])).ToList();
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
        await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<string>(
            "SELECT token_hash FROM sessions WHERE instance_id = @projectId AND id = @id", new { projectId, id }), cancellationToken) is { } tokenHash
        && await sessions.RevokeStoredAsync(projectId, tokenHash, cancellationToken);

    public async Task<IReadOnlyList<OidcApplication>> ClientsAsync(string projectId, CancellationToken cancellationToken) =>
        (await data.ReadAsync(connection => connection.QueryAsync<OidcApplication>(
            "SELECT * FROM oidc_applications WHERE instance_id = @projectId ORDER BY id", new { projectId }), cancellationToken)).AsList();

    public async Task<(OidcApplication Client, string? Secret)> CreateClientAsync(HttpContext context, UserRecord caller, string projectId, NewClient client, IOpenIddictApplicationManager manager, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.StepUpBindings.Count == 0)
        {
            throw StepUpProofs.Required();
        }
        var (type, policy) = (client.Type, client.Policy);
        var secret = type is ClientTypes.Confidential or "machine" ? Tokens.RandomToken(48) : null;
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = AuthIds.New(),
            ClientType = policy is null ? type : ClientTypes.Confidential,
            ApplicationType = ApplicationTypes.Web,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = client.Name,
            Permissions =
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession, Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.OpenId, Permissions.Scopes.Email, Permissions.Scopes.Profile, Permissions.Prefixes.Scope + Scopes.OfflineAccess,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        if (policy is not null)
        {
            MachineClients.Configure(descriptor, policy);
        }
        else if (client.AllowAnonymous)
        {
            descriptor.Permissions.Add(GuestSignIn.Permission);
        }

        descriptor.RedirectUris.UnionWith(client.RedirectUris);
        descriptor.PostLogoutRedirectUris.UnionWith(client.RedirectUris);
        var application = new OidcApplication { AllowedOrigins = client.AllowedOrigins };
        await manager.PopulateAsync(application, descriptor, cancellationToken);
        var previous = context.Items[Tenancy.ItemKey];
        context.Items[Tenancy.ItemKey] = projectId;
        context.Items[ClientIssuanceCaller] = caller;
        try
        {
            await manager.CreateAsync(application, secret, cancellationToken);
            return (application, secret);
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
        var project = await platform.ProjectAsync(projectId, cancellationToken) ?? throw PlatformDirectory.NotFound();
        await data.SaveAsync(platform.Audit(project.Value.OrganizationId, userId, "oauth_client.rotated", clientId), cancellationToken);
    }

    public async Task<bool> DeleteClientAsync(string projectId, string clientId, CancellationToken cancellationToken) =>
        await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<OidcApplication>(
            "SELECT * FROM oidc_applications WHERE instance_id = @projectId AND id = @clientId", new { projectId, clientId }), cancellationToken) is { } client
        && await data.SaveAsync(Change.Delete(client), cancellationToken);
}
