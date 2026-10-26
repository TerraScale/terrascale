using System.Text.Json;
using System.Text.Json.Nodes;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary>A live session of a project tenant.</summary>
/// <param name="Id">Its TerraBase document id; never the token or its hash.</param>
/// <param name="Session">The session.</param>
/// <param name="Email">Its user's email.</param>
public sealed record ProjectSession(string Id, SessionRecord Session, string Email);

/// <summary>
/// A project tenant's end users, sessions and OAuth clients, managed by the project's platform members. Every record
/// lives in the project's own instance (its id), so no call reaches another tenant's data.
/// </summary>
public sealed class TenantDirectory(AuthStore store, Users users, Sessions sessions, PlatformDirectory platform, TimeProvider clock)
{
    internal const string ClientIssuanceCaller = "ts-auth:client-issuance-caller";
    // Bounds the pages a filtered list reads for one answer; a sparse match answers fewer items and a cursor.
    private const int MaxRounds = 10;

    /// <summary>Checks that <paramref name="userId"/> holds at least <paramref name="minimum"/> on the project: 404 when it holds none, 403 when lower.</summary>
    public async Task CheckAccessAsync(string userId, string projectId, string minimum, CancellationToken cancellationToken)
    {
        var project = await platform.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        PlatformDirectory.RequireRole(await platform.ProjectAccessAsync(userId, project.Value.OrganizationId, projectId, cancellationToken).ConfigureAwait(false), minimum);
    }

    /// <summary>A page of the tenant's users whose email contains <paramref name="query"/> (any when null).</summary>
    public Task<Page<UserRecord>> UsersAsync(string projectId, string? query, int limit, string? after, CancellationToken cancellationToken)
    {
        var needle = string.IsNullOrWhiteSpace(query) ? null : query.Trim().ToLowerInvariant();
        return this.ScanAsync(AuthStore.UserCollection, projectId, [], limit, after, document => Task.FromResult(
            document.Document.Deserialize(IdentityJson.Default.UserRecord) is { } user && user.InstanceId == projectId
            && (needle is null || user.Email.Contains(needle, StringComparison.Ordinal)) ? user : null), cancellationToken);
    }

    /// <summary>The tenant's user, or 404.</summary>
    public async Task<UserRecord> UserAsync(string projectId, string userId, CancellationToken cancellationToken) =>
        await users.GetAsync(projectId, userId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();

    /// <summary>Disables or enables the tenant's user, or 404.</summary>
    public async Task<UserRecord> SetDisabledAsync(string projectId, string userId, bool disabled, CancellationToken cancellationToken) =>
        await users.SetDisabledAsync(projectId, userId, disabled, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();

    /// <summary>A page of the tenant's live sessions (unrevoked, unexpired, of an enabled user at its current epoch), with each document id.</summary>
    public async Task<Page<ProjectSession>> SessionsAsync(string projectId, string? userId, int limit, string? after, CancellationToken cancellationToken)
    {
        var lifetime = await sessions.GetLifetimeAsync(projectId, cancellationToken).ConfigureAwait(false);
        var now = JsonSerializer.SerializeToNode(clock.GetUtcNow(), IdentityJson.Default.DateTimeOffset);
        List<JsonObject> conditions = [new() { ["eq"] = new JsonArray("/revoked", false) }, new() { ["gt"] = new JsonArray("/expiresAt", now) }];
        if (userId is not null)
        {
            conditions.Add(PlatformDirectory.Equal("/userId", userId));
        }

        var owners = new Dictionary<string, UserRecord?>(StringComparer.Ordinal);
        return await this.ScanAsync(AuthStore.SessionCollection, projectId, conditions, limit, after, async document =>
        {
            if (document.Document.Deserialize(IdentityJson.Default.SessionRecord) is not { } session || session.InstanceId != projectId)
            {
                return null;
            }

            var policyExpiry = session.CreatedAt + lifetime;
            session = session with { ExpiresAt = policyExpiry < session.ExpiresAt ? policyExpiry : session.ExpiresAt };
            if (session.ExpiresAt <= clock.GetUtcNow())
            {
                return null;
            }

            if (!owners.TryGetValue(session.UserId, out var user))
            {
                owners[session.UserId] = user = await users.GetAsync(projectId, session.UserId, cancellationToken).ConfigureAwait(false);
            }

            return user is { Disabled: false } && user.AuthEpoch == session.AuthEpoch && user.AuthStamp == session.AuthStamp
                ? new ProjectSession(document.Id, session, user.Email) : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes the tenant's session with document id <paramref name="id"/>; false when there is none.</summary>
    public async Task<bool> RevokeSessionAsync(string projectId, string id, CancellationToken cancellationToken)
    {
        var filter = PlatformDirectory.Filter("and", new JsonArray(PlatformDirectory.Equal("/instanceId", projectId), PlatformDirectory.Equal("$id", id)));
        return await store.QueryAsync(AuthStore.SessionCollection, filter, 1, cancellationToken).ConfigureAwait(false) is [var document]
            && document.Document.Deserialize(IdentityJson.Default.SessionRecord) is { } session && session.InstanceId == projectId
            && await sessions.RevokeStoredAsync(projectId, session.TokenHash, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tenant's OAuth clients.</summary>
    public async Task<IReadOnlyList<OidcApplication>> ClientsAsync(string projectId, CancellationToken cancellationToken)
    {
        List<OidcApplication> clients = [];
        await foreach (var document in store.QueryAsync(AuthStore.OidcApplicationCollection, projectId, "/instanceId", projectId, cancellationToken).ConfigureAwait(false))
        {
            if (document.Document.Deserialize(OidcJson.Default.OidcApplication) is { ClientId: { } clientId } client && client.InstanceId == projectId && client.Id == clientId)
            {
                clients.Add(client);
            }
        }

        return clients;
    }

    /// <summary>
    /// Registers an authorization-code client with S256 PKCE, refresh tokens and logout through OpenIddict's application
    /// manager, with the request's tenant set to the project so its store writes into the project's instance. A
    /// confidential client's secret is returned once; OpenIddict keeps only its hash.
    /// </summary>
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
            ClientId = AuthStore.NewId(),
            ClientSecret = secret,
            ClientType = type,
            ApplicationType = ApplicationTypes.Web,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = name,
            Permissions =
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession,
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

    /// <summary>Deletes the tenant's client; false when there is none.</summary>
    public async Task<bool> DeleteClientAsync(string projectId, string clientId, CancellationToken cancellationToken) =>
        await store.GetAsync(AuthStore.OidcApplicationCollection, projectId, clientId, cancellationToken).ConfigureAwait(false) is { } document
        && document.Document.Deserialize(OidcJson.Default.OidcApplication) is { } client && client.InstanceId == projectId && client.ClientId == clientId
        && await store.CompareAndDeleteAsync(AuthStore.OidcApplicationCollection, projectId, clientId, document.Version, cancellationToken).ConfigureAwait(false);

    // Reads pages of the instance's matching documents in id order, each exactly as large as the items still missing,
    // so the answered cursor never skips an unread document.
    private async Task<Page<T>> ScanAsync<T>(string collection, string projectId, IReadOnlyList<JsonObject> conditions, int limit, string? after, Func<TerraBaseDocument, Task<T?>> select, CancellationToken cancellationToken)
        where T : class
    {
        List<T> items = [];
        var cursor = after;
        try
        {
            for (var round = 0; round < MaxRounds && items.Count < limit; round++)
            {
                var page = await store.PageAsync(collection, projectId, conditions, [], limit - items.Count, cursor, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var document in page.Items)
                {
                    if (await select(document).ConfigureAwait(false) is { } item)
                    {
                        items.Add(item);
                    }
                }

                if ((cursor = page.NextCursor) is null)
                {
                    break;
                }
            }
        }
        catch (TerraBaseException error) when (error.ErrorCode == "invalid_cursor")
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_request", "after is not a cursor of this list.");
        }

        return new(items, cursor);
    }
}
