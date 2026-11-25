using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.Agents;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Oidc;

public sealed class OidcApplicationStore(AuthData data, Credentials credentials, IServiceProvider services, IHttpContextAccessor http) : IOpenIddictApplicationStore<OidcApplication>
{
    private readonly record struct RequestCopy(string Tenant, string ClientId);

    private string Tenant => Tenancy.Current(http.HttpContext);

    public async ValueTask CreateAsync(OidcApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.ClientId = TerraScaleIds.Validate(application.ClientId);
        (application.Id, application.InstanceId) = (application.ClientId, this.Tenant);
        http.HttpContext?.Items.Remove(new RequestCopy(this.Tenant, application.ClientId));
        if (http.HttpContext?.Items[TenantDirectory.ClientIssuanceCaller] is UserRecord caller)
        {
            application.SecretBindings = caller.StepUpBindings;
            if (await services.GetRequiredService<StepUpProofs>().RequirementsAsync(caller, cancellationToken) is not { } requirements
                || !await data.SaveAsync([.. requirements, Change.Insert(application)], cancellationToken))
            {
                throw StepUpProofs.Required();
            }
        }
        else if (!await data.SaveAsync(Change.Insert(application), cancellationToken))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The client already exists.");
        }
    }

    public async ValueTask UpdateAsync(OidcApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        http.HttpContext?.Items.Remove(new RequestCopy(this.Tenant, application.Id!));
        if (!await data.SaveAsync(Change.Update(application), cancellationToken))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The client was changed by another request.");
        }
    }

    public async ValueTask DeleteAsync(OidcApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        http.HttpContext?.Items.Remove(new RequestCopy(this.Tenant, application.Id!));
        if (!await data.SaveAsync(Change.Delete(application), cancellationToken))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The client was changed by another request.");
        }
    }

    public async ValueTask<OidcApplication?> FindByClientIdAsync(string identifier, CancellationToken cancellationToken)
    {
        if (this.Tenant == AgentIdIssuer.Slug && AgentIdIssuer.OpenClient(identifier) is { } open)
        {
            return open;
        }

        if (!TerraScaleIds.IsValid(identifier))
        {
            return null;
        }

        var items = http.HttpContext?.Items;
        var key = new RequestCopy(this.Tenant, identifier);
        if (items is not null && items.TryGetValue(key, out var copy))
        {
            return (OidcApplication?)copy;
        }

        var application = await this.ReadAsync(identifier, cancellationToken);
        items?[key] = application;
        return application;
    }

    private async ValueTask<OidcApplication?> ReadAsync(string identifier, CancellationToken cancellationToken)
    {
        var tenant = this.Tenant;
        if (tenant == AgentIdIssuer.Slug)
        {
            var registration = await services.GetRequiredService<AgentIdClients>().FindAsync(identifier, cancellationToken);
            http.HttpContext?.Items[new AgentIdRegistration.RequestCopy(identifier)] = registration;
            return registration?.Application;
        }

        var application = await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<OidcApplication>(
            "SELECT * FROM oidc_applications WHERE instance_id = @tenant AND id = @identifier", new { tenant, identifier }), cancellationToken);
        if (application is null)
        {
            if (tenant != OidcIssuer.InstanceId)
            {
                return null;
            }

            var keys = services.GetRequiredService<Platform.ApiKeys>();
            return await keys.FindAsync(identifier, cancellationToken) is { } key ? Platform.ApiKeyClients.Describe(key, keys.Digests(key)) : null;
        }

        if (application.ClientId != identifier)
        {
            return null;
        }

        if (application.ClientSecret is not null && Tenancy.IsProject(tenant)
            && application.SecretBindings.Count > 0 && !await credentials.AreCurrentAsync(PlatformDirectory.Instance, application.SecretBindings, cancellationToken))
        {
            return null;
        }

        return application;
    }

    public ValueTask<OidcApplication?> FindByIdAsync(string identifier, CancellationToken cancellationToken) => this.FindByClientIdAsync(identifier, cancellationToken);

    public IAsyncEnumerable<OidcApplication> FindByPostLogoutRedirectUriAsync(string uri, CancellationToken cancellationToken) =>
        this.AllAsync(cancellationToken).Where(application => application.PostLogoutRedirectUris.Contains(uri, StringComparer.Ordinal));

    public IAsyncEnumerable<OidcApplication> FindByRedirectUriAsync(string uri, CancellationToken cancellationToken) =>
        this.AllAsync(cancellationToken).Where(application => application.RedirectUris.Contains(uri, StringComparer.Ordinal));

    public ValueTask<OidcApplication> InstantiateAsync(CancellationToken cancellationToken) => new(new OidcApplication());

    public ValueTask<string?> GetApplicationTypeAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.ApplicationType);

    public ValueTask<string?> GetClientIdAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.ClientId);

    public ValueTask<string?> GetClientSecretAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.ClientSecret);

    public ValueTask<string?> GetClientTypeAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.ClientType);

    public ValueTask<string?> GetConsentTypeAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.ConsentType);

    public ValueTask<string?> GetDisplayNameAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.DisplayName);

    public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(OidcApplication application, CancellationToken cancellationToken) =>
        new(ImmutableDictionary<CultureInfo, string>.Empty);

    public ValueTask<string?> GetIdAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.Id);

    public ValueTask<JsonWebKeySet?> GetJsonWebKeySetAsync(OidcApplication application, CancellationToken cancellationToken) => new((JsonWebKeySet?)null);

    public ValueTask<ImmutableArray<string>> GetPermissionsAsync(OidcApplication application, CancellationToken cancellationToken) => new([.. application.Permissions]);

    public ValueTask<ImmutableArray<string>> GetPostLogoutRedirectUrisAsync(OidcApplication application, CancellationToken cancellationToken) => new([.. application.PostLogoutRedirectUris]);

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.Properties.ToImmutableDictionary());

    public ValueTask<ImmutableArray<string>> GetRedirectUrisAsync(OidcApplication application, CancellationToken cancellationToken) => new([.. application.RedirectUris]);

    public ValueTask<ImmutableArray<string>> GetRequirementsAsync(OidcApplication application, CancellationToken cancellationToken) => new([.. application.Requirements]);

    public ValueTask<ImmutableDictionary<string, string>> GetSettingsAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.Settings.ToImmutableDictionary());

    public ValueTask SetApplicationTypeAsync(OidcApplication application, string? type, CancellationToken cancellationToken) => Set(() => application.ApplicationType = type);

    public ValueTask SetClientIdAsync(OidcApplication application, string? identifier, CancellationToken cancellationToken) => Set(() => application.ClientId = identifier);

    public ValueTask SetClientSecretAsync(OidcApplication application, string? secret, CancellationToken cancellationToken) => Set(() => application.ClientSecret = secret);

    public ValueTask SetClientTypeAsync(OidcApplication application, string? type, CancellationToken cancellationToken) => Set(() => application.ClientType = type);

    public ValueTask SetConsentTypeAsync(OidcApplication application, string? type, CancellationToken cancellationToken) => Set(() => application.ConsentType = type);

    public ValueTask SetDisplayNameAsync(OidcApplication application, string? name, CancellationToken cancellationToken) => Set(() => application.DisplayName = name);

    public ValueTask SetDisplayNamesAsync(OidcApplication application, ImmutableDictionary<CultureInfo, string> names, CancellationToken cancellationToken) =>
        names.IsEmpty ? default : throw new NotSupportedException("Localized client names are not supported.");

    public ValueTask SetJsonWebKeySetAsync(OidcApplication application, JsonWebKeySet? set, CancellationToken cancellationToken) =>
        set is null ? default : throw new NotSupportedException("Client key sets are not supported.");

    public ValueTask SetPermissionsAsync(OidcApplication application, ImmutableArray<string> permissions, CancellationToken cancellationToken) => Set(() => application.Permissions = [.. permissions]);

    public ValueTask SetPostLogoutRedirectUrisAsync(OidcApplication application, ImmutableArray<string> uris, CancellationToken cancellationToken) => Set(() => application.PostLogoutRedirectUris = [.. uris]);

    public ValueTask SetPropertiesAsync(OidcApplication application, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken) => Set(() => application.Properties = properties);

    public ValueTask SetRedirectUrisAsync(OidcApplication application, ImmutableArray<string> uris, CancellationToken cancellationToken) => Set(() => application.RedirectUris = [.. uris]);

    public ValueTask SetRequirementsAsync(OidcApplication application, ImmutableArray<string> requirements, CancellationToken cancellationToken) => Set(() => application.Requirements = [.. requirements]);

    public ValueTask SetSettingsAsync(OidcApplication application, ImmutableDictionary<string, string> settings, CancellationToken cancellationToken) => Set(() => application.Settings = settings);

    public ValueTask<long> CountAsync(CancellationToken cancellationToken) => throw Unsupported();

    public ValueTask<long> CountAsync<TResult>(Func<IQueryable<OidcApplication>, IQueryable<TResult>> query, CancellationToken cancellationToken) => throw Unsupported();

    public ValueTask<TResult?> GetAsync<TState, TResult>(Func<IQueryable<OidcApplication>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken) => throw Unsupported();

    public IAsyncEnumerable<OidcApplication> ListAsync(int? count, int? offset, CancellationToken cancellationToken) => throw Unsupported();

    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(Func<IQueryable<OidcApplication>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken) => throw Unsupported();

    private async IAsyncEnumerable<OidcApplication> AllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tenant = this.Tenant;
        foreach (var application in await data.ReadAsync(connection => connection.QueryAsync<OidcApplication>(
            "SELECT * FROM oidc_applications WHERE instance_id = @tenant", new { tenant }), cancellationToken))
        {
            yield return application;
        }
    }

    internal static ValueTask Set(Action assign)
    {
        assign();
        return default;
    }

    internal static NotSupportedException Unsupported() => new("This OpenIddict store operation is not supported by the TerraBase stores.");
}

public sealed class OidcAuthorizationStore(AuthData data, IHttpContextAccessor http, TimeProvider clock) : IOpenIddictAuthorizationStore<OidcAuthorization>
{
    private const int RevokeRounds = 3;

    private string Tenant => Tenancy.Current(http.HttpContext);

    public async ValueTask CreateAsync(OidcAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        (authorization.Id, authorization.InstanceId) = (AuthIds.New(), this.Tenant);
        if (!await data.SaveAsync(Change.Insert(authorization), cancellationToken))
        {
            throw ApiException.Unavailable();
        }
    }

    public async ValueTask UpdateAsync(OidcAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!await data.SaveAsync(Change.Update(authorization), cancellationToken))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The authorization was changed by another request.");
        }
    }

    public async ValueTask DeleteAsync(OidcAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!await data.SaveAsync(Change.Delete(authorization), cancellationToken))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The authorization was changed by another request.");
        }
    }

    public async ValueTask<OidcAuthorization?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(identifier))
        {
            return null;
        }

        var tenant = this.Tenant;
        return await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<OidcAuthorization>(
            "SELECT * FROM oidc_authorizations WHERE instance_id = @tenant AND id = @identifier", new { tenant, identifier }), cancellationToken);
    }

    public IAsyncEnumerable<OidcAuthorization> FindAsync(string? subject, string? client, string? status, string? type, ImmutableArray<string>? scopes, CancellationToken cancellationToken) =>
        (subject is null && client is null ? throw OidcApplicationStore.Unsupported() : this.FindAsync(subject, client, status, type, cancellationToken))
            .Where(authorization => scopes is null || scopes.Value.All(authorization.Scopes.Contains));

    public IAsyncEnumerable<OidcAuthorization> FindByApplicationIdAsync(string identifier, CancellationToken cancellationToken) => this.FindAsync(null, identifier, null, null, cancellationToken);

    public IAsyncEnumerable<OidcAuthorization> FindBySubjectAsync(string subject, CancellationToken cancellationToken) => this.FindAsync(subject, null, null, null, cancellationToken);

    public ValueTask<long> RevokeAsync(string? subject, string? client, string? status, string? type, CancellationToken cancellationToken) =>
        this.RevokeAllAsync(this.FindAsync(subject, client, status, type, null, cancellationToken), cancellationToken);

    public ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken) => this.RevokeAllAsync(this.FindByApplicationIdAsync(identifier, cancellationToken), cancellationToken);

    public ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken) => this.RevokeAllAsync(this.FindBySubjectAsync(subject, cancellationToken), cancellationToken);

    public ValueTask<OidcAuthorization> InstantiateAsync(CancellationToken cancellationToken) => new(new OidcAuthorization());

    public ValueTask<string?> GetApplicationIdAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.ApplicationId);

    public ValueTask<DateTimeOffset?> GetCreationDateAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.CreationDate);

    public ValueTask<string?> GetIdAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.Id);

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.Properties.ToImmutableDictionary());

    public ValueTask<ImmutableArray<string>> GetScopesAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new([.. authorization.Scopes]);

    public ValueTask<string?> GetStatusAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.Status);

    public ValueTask<string?> GetSubjectAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.Subject);

    public ValueTask<string?> GetTypeAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.Type);

    public ValueTask SetApplicationIdAsync(OidcAuthorization authorization, string? identifier, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.ApplicationId = identifier);

    public ValueTask SetCreationDateAsync(OidcAuthorization authorization, DateTimeOffset? date, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.CreationDate = date);

    public ValueTask SetPropertiesAsync(OidcAuthorization authorization, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.Properties = properties);

    public ValueTask SetScopesAsync(OidcAuthorization authorization, ImmutableArray<string> scopes, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.Scopes = [.. scopes]);

    public ValueTask SetStatusAsync(OidcAuthorization authorization, string? status, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.Status = status);

    public ValueTask SetSubjectAsync(OidcAuthorization authorization, string? subject, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.Subject = subject);

    public ValueTask SetTypeAsync(OidcAuthorization authorization, string? type, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.Type = type);

    public ValueTask<long> CountAsync(CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    public ValueTask<long> CountAsync<TResult>(Func<IQueryable<OidcAuthorization>, IQueryable<TResult>> query, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    public ValueTask<TResult?> GetAsync<TState, TResult>(Func<IQueryable<OidcAuthorization>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    public IAsyncEnumerable<OidcAuthorization> ListAsync(int? count, int? offset, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(Func<IQueryable<OidcAuthorization>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    public ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromHours(1);
        var (revoked, adHoc, approval, slug) = (Statuses.Revoked, AuthorizationTypes.AdHoc, RedeemExactlyOnce.DeviceApprovalKind, OidcIssuer.Slug);
        return await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
                """
                DELETE FROM oidc_authorizations WHERE id IN (
                    SELECT a.id FROM oidc_authorizations a
                    WHERE (a.status = @revoked OR a.type = @adHoc) AND a.creation_date <= @cutoff
                        AND NOT EXISTS (SELECT 1 FROM oidc_tokens t WHERE t.authorization_id = a.id)
                        AND NOT EXISTS (SELECT 1 FROM credential_claims c WHERE c.instance_id = a.instance_id AND c.kind = @approval AND c.owner_id = a.id
                            AND EXISTS (SELECT 1 FROM oidc_authorizations p WHERE p.id = c."key"))
                    ORDER BY a.creation_date LIMIT @batchSize)
                """, new { revoked, adHoc, cutoff, approval, batchSize }), cancellationToken)
            + await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
                """
                DELETE FROM credential_claims WHERE instance_id = @slug AND kind = @approval AND "key" IN (
                    SELECT s."key" FROM credential_claims s
                    WHERE s.instance_id = @slug AND s.kind = @approval AND NOT EXISTS (SELECT 1 FROM oidc_authorizations p WHERE p.id = s."key")
                    ORDER BY s."key" LIMIT @batchSize)
                """, new { slug, approval, batchSize }), cancellationToken);
    }

    private async IAsyncEnumerable<OidcAuthorization> FindAsync(string? subject, string? client, string? status, string? type, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tenant = this.Tenant;
        foreach (var authorization in await data.ReadAsync(connection => connection.QueryAsync<OidcAuthorization>(
            """
            SELECT * FROM oidc_authorizations WHERE instance_id = @tenant AND (@subject IS NULL OR subject = @subject) AND (@client IS NULL OR application_id = @client)
                AND (@status IS NULL OR status = @status) AND (@type IS NULL OR type = @type)
            """, new { tenant, subject, client, status, type }), cancellationToken))
        {
            yield return authorization;
        }
    }

    private async ValueTask<long> RevokeAllAsync(IAsyncEnumerable<OidcAuthorization> authorizations, CancellationToken cancellationToken)
    {
        long revoked = 0;
        await foreach (var authorization in authorizations)
        {
            if (await this.RevokeOneAsync(authorization, cancellationToken))
            {
                revoked++;
            }
        }

        return revoked;
    }

    private async Task<bool> RevokeOneAsync(OidcAuthorization authorization, CancellationToken cancellationToken)
    {
        if (authorization.Id is not { } id)
        {
            throw new InvalidOperationException("A stored authorization has no id.");
        }

        var current = authorization;
        for (var round = 0; round < RevokeRounds; round++)
        {
            if (current.Status == Statuses.Revoked)
            {
                return false;
            }

            current.Status = Statuses.Revoked;
            if (await data.SaveAsync(Change.Update(current), cancellationToken))
            {
                return true;
            }

            if (await this.FindByIdAsync(id, cancellationToken) is not { } reread)
            {
                return false;
            }

            current = reread;
        }

        throw ApiException.Unavailable();
    }
}

public sealed class OidcTokenStore(AuthData data, Credentials credentials, IOpenIddictAuthorizationStore<OidcAuthorization> authorizations, IHttpContextAccessor http, TimeProvider clock) : IOpenIddictTokenStore<OidcToken>
{
    private const int RevokeRounds = 3;

    private string Tenant => Tenancy.Current(http.HttpContext);

    public async ValueTask CreateAsync(OidcToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        (token.Id, token.InstanceId) = (AuthIds.New(), this.Tenant);
        if (CredentialLineage.Issued(http.HttpContext) is { Count: > 0 } bindings)
        {
            token.Bindings = bindings;
        }

        if (http.HttpContext?.Items[AgentExchange.Fence] is LiveAgent fence)
        {
            if (await data.SaveAsync([Change.Insert(token), Change.Assert(fence.Agent), Change.Assert(fence.Owner)], cancellationToken))
            {
                token.StoredReferenceId = token.ReferenceId;
                return;
            }

            http.HttpContext.Items[AgentExchange.FenceBroken] = true;
            token.Status = Statuses.Revoked;
        }

        if (!await data.SaveAsync(Change.Insert(token), cancellationToken))
        {
            throw new InvalidOperationException("The token reference is already taken.");
        }

        token.StoredReferenceId = token.ReferenceId;
    }

    public async ValueTask UpdateAsync(OidcToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.StoredReferenceId is not null && token.ReferenceId != token.StoredReferenceId)
        {
            throw new NotSupportedException("A token reference never changes.");
        }

        if (!await data.SaveAsync(Change.Update(token), cancellationToken))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The token was changed by another request.");
        }

        token.StoredReferenceId = token.ReferenceId;
    }

    public async ValueTask DeleteAsync(OidcToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!await data.SaveAsync(Change.Delete(token), cancellationToken))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The token was changed by another request.");
        }
    }

    public async ValueTask<OidcToken?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(identifier))
        {
            return null;
        }

        if (await this.ReadAsync(identifier, cancellationToken) is not { } token)
        {
            return null;
        }

        if (!await credentials.AreCurrentAsync(this.Tenant, token.Bindings, cancellationToken))
        {
            token.Status = Statuses.Revoked;
        }

        return token;
    }

    internal async ValueTask<bool> IsPersistedRefreshRedeemedAsync(string identifier, string authorizationId, string clientId, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(identifier))
        {
            return false;
        }

        return await this.ReadAsync(identifier, cancellationToken) is { Status: Statuses.Redeemed, Type: TokenTypeIdentifiers.RefreshToken } token
            && token.Id == identifier && token.AuthorizationId == authorizationId && token.ApplicationId == clientId;
    }

    public async ValueTask<OidcToken?> FindByReferenceIdAsync(string identifier, CancellationToken cancellationToken) =>
        await this.OwnerAsync(identifier, cancellationToken) is { } owner
            && await this.FindByIdAsync(owner, cancellationToken) is { } token && token.ReferenceId == identifier
            ? token
            : null;

    public IAsyncEnumerable<OidcToken> FindAsync(string? subject, string? client, string? status, string? type, CancellationToken cancellationToken) =>
        subject is null && client is null ? throw OidcApplicationStore.Unsupported() : this.FindAsync(subject, client, null, status, type, cancellationToken);

    public IAsyncEnumerable<OidcToken> FindByApplicationIdAsync(string identifier, CancellationToken cancellationToken) => this.FindAsync(null, identifier, null, null, null, cancellationToken);

    public IAsyncEnumerable<OidcToken> FindByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken) => this.FindAsync(null, null, identifier, null, null, cancellationToken);

    public IAsyncEnumerable<OidcToken> FindBySubjectAsync(string subject, CancellationToken cancellationToken) => this.FindAsync(subject, null, null, null, null, cancellationToken);

    public ValueTask<long> RevokeAsync(string? subject, string? client, string? status, string? type, CancellationToken cancellationToken) =>
        this.RevokeAllAsync(this.FindAsync(subject, client, status, type, cancellationToken), cancellationToken);

    public ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken = default) => this.RevokeAllAsync(this.FindByApplicationIdAsync(identifier, cancellationToken), cancellationToken);

    public async ValueTask<long> RevokeByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken)
    {
        for (var round = 0; round < 3; round++)
        {
            if (await authorizations.FindByIdAsync(identifier, cancellationToken) is not { } authorization)
            {
                return 0;
            }

            if (authorization.Status == Statuses.Revoked)
            {
                return 1;
            }

            authorization.Status = Statuses.Revoked;
            try
            {
                await authorizations.UpdateAsync(authorization, cancellationToken);
                return 1;
            }
            catch (OpenIddictExceptions.ConcurrencyException)
            {
            }
        }

        throw ApiException.Unavailable();
    }

    public ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken = default) => this.RevokeAllAsync(this.FindBySubjectAsync(subject, cancellationToken), cancellationToken);

    public ValueTask<OidcToken> InstantiateAsync(CancellationToken cancellationToken) => new(new OidcToken());

    public ValueTask<string?> GetApplicationIdAsync(OidcToken token, CancellationToken cancellationToken) => new(token.ApplicationId);

    public ValueTask<string?> GetAuthorizationIdAsync(OidcToken token, CancellationToken cancellationToken) => new(token.AuthorizationId);

    public ValueTask<DateTimeOffset?> GetCreationDateAsync(OidcToken token, CancellationToken cancellationToken) => new(token.CreationDate);

    public ValueTask<DateTimeOffset?> GetExpirationDateAsync(OidcToken token, CancellationToken cancellationToken) => new(token.ExpirationDate);

    public ValueTask<string?> GetIdAsync(OidcToken token, CancellationToken cancellationToken) => new(token.Id);

    public ValueTask<string?> GetPayloadAsync(OidcToken token, CancellationToken cancellationToken) => new(token.Payload);

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OidcToken token, CancellationToken cancellationToken) => new(token.Properties.ToImmutableDictionary());

    public ValueTask<DateTimeOffset?> GetRedemptionDateAsync(OidcToken token, CancellationToken cancellationToken) => new(token.RedemptionDate);

    public ValueTask<string?> GetReferenceIdAsync(OidcToken token, CancellationToken cancellationToken) => new(token.ReferenceId);

    public ValueTask<string?> GetStatusAsync(OidcToken token, CancellationToken cancellationToken) => new(token.Status);

    public ValueTask<string?> GetSubjectAsync(OidcToken token, CancellationToken cancellationToken) => new(token.Subject);

    public ValueTask<string?> GetTypeAsync(OidcToken token, CancellationToken cancellationToken) => new(token.Type);

    public ValueTask SetApplicationIdAsync(OidcToken token, string? identifier, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.ApplicationId = identifier);

    public ValueTask SetAuthorizationIdAsync(OidcToken token, string? identifier, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.AuthorizationId = identifier);

    public ValueTask SetCreationDateAsync(OidcToken token, DateTimeOffset? date, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.CreationDate = date);

    public ValueTask SetExpirationDateAsync(OidcToken token, DateTimeOffset? date, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.ExpirationDate = date);

    public ValueTask SetPayloadAsync(OidcToken token, string? payload, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.Payload = payload);

    public ValueTask SetPropertiesAsync(OidcToken token, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.Properties = properties);

    public ValueTask SetRedemptionDateAsync(OidcToken token, DateTimeOffset? date, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.RedemptionDate = date);

    public ValueTask SetReferenceIdAsync(OidcToken token, string? identifier, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.ReferenceId = identifier);

    public ValueTask SetStatusAsync(OidcToken token, string? status, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.Status = status);

    public ValueTask SetSubjectAsync(OidcToken token, string? subject, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.Subject = subject);

    public ValueTask SetTypeAsync(OidcToken token, string? type, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.Type = type);

    public ValueTask<long> CountAsync(CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    public ValueTask<long> CountAsync<TResult>(Func<IQueryable<OidcToken>, IQueryable<TResult>> query, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    public ValueTask<TResult?> GetAsync<TState, TResult>(Func<IQueryable<OidcToken>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    public IAsyncEnumerable<OidcToken> ListAsync(int? count, int? offset, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(Func<IQueryable<OidcToken>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    public ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var (revokedStatus, refresh) = (Statuses.Revoked, TokenTypeIdentifiers.RefreshToken);
        var deleted = await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            "DELETE FROM oidc_tokens WHERE id IN (SELECT id FROM oidc_tokens WHERE expiration_date <= @now ORDER BY expiration_date LIMIT @batchSize)",
            new { now, batchSize }), cancellationToken);
        return deleted + await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            """
            DELETE FROM oidc_tokens WHERE id IN (
                SELECT t.id FROM oidc_tokens t
                WHERE t.type = @refresh AND t.authorization_id IN (SELECT a.id FROM oidc_authorizations a WHERE a.status = @revokedStatus)
                LIMIT @batchSize)
            """, new { revokedStatus, refresh, batchSize }), cancellationToken);
    }

    private async IAsyncEnumerable<OidcToken> FindAsync(string? subject, string? client, string? authorization, string? status, string? type,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tenant = this.Tenant;
        foreach (var token in await data.ReadAsync(connection => connection.QueryAsync<OidcToken>(
            """
            SELECT * FROM oidc_tokens WHERE instance_id = @tenant AND (@subject IS NULL OR subject = @subject) AND (@client IS NULL OR application_id = @client)
                AND (@authorization IS NULL OR authorization_id = @authorization) AND (@status IS NULL OR status = @status) AND (@type IS NULL OR type = @type)
            """, new { tenant, subject, client, authorization, status, type }), cancellationToken))
        {
            token.StoredReferenceId = token.ReferenceId;
            yield return token;
        }
    }

    private async Task<OidcToken?> ReadAsync(string identifier, CancellationToken cancellationToken)
    {
        var tenant = this.Tenant;
        var token = await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<OidcToken>(
            "SELECT * FROM oidc_tokens WHERE instance_id = @tenant AND id = @identifier", new { tenant, identifier }), cancellationToken);
        token?.StoredReferenceId = token.ReferenceId;
        return token;
    }

    private Task<string?> OwnerAsync(string reference, CancellationToken cancellationToken)
    {
        var tenant = this.Tenant;
        return data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<string>(
            "SELECT id FROM oidc_tokens WHERE instance_id = @tenant AND reference_id = @reference", new { tenant, reference }), cancellationToken);
    }

    private async ValueTask<long> RevokeAllAsync(IAsyncEnumerable<OidcToken> tokens, CancellationToken cancellationToken)
    {
        long revoked = 0;
        await foreach (var token in tokens)
        {
            if (await this.RevokeOneAsync(token, cancellationToken))
            {
                revoked++;
            }
        }

        return revoked;
    }

    private async Task<bool> RevokeOneAsync(OidcToken token, CancellationToken cancellationToken)
    {
        if (token.Id is not { } id)
        {
            throw new InvalidOperationException("A stored token has no id.");
        }

        var current = token;
        for (var round = 0; round < RevokeRounds; round++)
        {
            if (current.Status is Statuses.Revoked)
            {
                return false;
            }

            current.Status = Statuses.Revoked;
            if (await data.SaveAsync(Change.Update(current), cancellationToken))
            {
                return true;
            }

            if (await this.ReadAsync(id, cancellationToken) is not { } reread)
            {
                return false;
            }

            current = reread;
        }

        throw ApiException.Unavailable();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OAuthErrorBody))]
internal sealed partial class OidcJson : JsonSerializerContext;
