using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Store;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>
/// OpenIddict clients on TerraBase, addressed by instance and client id. Every write is create-only or names the
/// version it read. LINQ queries, counting and paging are not supported: no OpenIddict server flow uses them.
/// </summary>
public sealed class OidcApplicationStore(AuthStore store, IServiceProvider services, IHttpContextAccessor http) : IOpenIddictApplicationStore<OidcApplication>
{
    private const string Collection = AuthStore.OidcApplicationCollection;

    // The HttpContext.Items key of this request's copy of one client.
    private readonly record struct RequestCopy(string Tenant, string ClientId);

    // The tenant of the request being served (Tenancy); the platform's outside a tenant request.
    private string Tenant => Tenancy.Current(http.HttpContext);

    /// <inheritdoc />
    public async ValueTask CreateAsync(OidcApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.ClientId = TerraScaleIds.Validate(application.ClientId);
        (application.Id, application.InstanceId) = (application.ClientId, this.Tenant);
        http.HttpContext?.Items.Remove(new RequestCopy(this.Tenant, application.ClientId));
        if (http.HttpContext?.Items[TenantDirectory.ClientIssuanceCaller] is UserRecord caller)
        {
            application.SecretBindings = caller.StepUpBindings;
            if (await store.TransactAsync([.. StepUpProofs.Requirements(caller), AuthStore.Create(Collection, this.Tenant, application.ClientId, Serialize(application))], cancellationToken).ConfigureAwait(false) is not null)
            {
                throw StepUpProofs.Required();
            }

            application.Version = (await store.GetAsync(Collection, this.Tenant, application.ClientId, cancellationToken).ConfigureAwait(false))?.Version
                ?? throw StepUpProofs.Required();
        }
        else
        {
            application.Version = (await store.CreateAsync(Collection, this.Tenant, application.ClientId, Serialize(application), cancellationToken).ConfigureAwait(false)).Version;
        }
    }

    /// <inheritdoc />
    public async ValueTask UpdateAsync(OidcApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        http.HttpContext?.Items.Remove(new RequestCopy(this.Tenant, application.Id!));
        application.Version = await store.ReplaceAsync(Collection, this.Tenant, application.Id!, Serialize(application), application.Version!, cancellationToken).ConfigureAwait(false)
            ?? throw new OpenIddictExceptions.ConcurrencyException("The client was changed by another request.");
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(OidcApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        http.HttpContext?.Items.Remove(new RequestCopy(this.Tenant, application.Id!));
        if (!await store.CompareAndDeleteAsync(Collection, this.Tenant, application.Id!, application.Version!, cancellationToken).ConfigureAwait(false))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The client was changed by another request.");
        }
    }

    /// <summary>
    /// The client, read from storage once per request: OpenIddict looks the same client up six to eight times while it
    /// serves one token or authorization request. Entity caching stays off globally (see <see cref="OidcServer"/>), so
    /// nothing is reused across requests; this request's own writes replace or drop its copy.
    /// </summary>
    public async ValueTask<OidcApplication?> FindByClientIdAsync(string identifier, CancellationToken cancellationToken)
    {
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

        var application = await this.ReadAsync(identifier, cancellationToken).ConfigureAwait(false);
        items?[key] = application;
        return application;
    }

    private async ValueTask<OidcApplication?> ReadAsync(string identifier, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(Collection, this.Tenant, identifier, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            var keys = services.GetRequiredService<Platform.ApiKeys>();
            return await keys.FindAsync(identifier, cancellationToken).ConfigureAwait(false) is { } key ? Platform.ApiKeyClients.Describe(key, keys.Digests(key)) : null;
        }

        var application = document.Document.Deserialize(OidcJson.Default.OidcApplication);
        if (application is null || application.InstanceId != this.Tenant || application.ClientId != identifier || application.Id != identifier)
        {
            return null;
        }

        // As for API keys: a client secret issued before fresh-proof lineage existed has none to lose and keeps working.
        if (application.ClientSecret is not null && Tenancy.IsProject(application.InstanceId)
            && application.SecretBindings.Count > 0 && !await store.AreCredentialsCurrentAsync(PlatformDirectory.Instance, application.SecretBindings, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        application.Version = document!.Version;
        return application;
    }

    /// <inheritdoc />
    public ValueTask<OidcApplication?> FindByIdAsync(string identifier, CancellationToken cancellationToken) => this.FindByClientIdAsync(identifier, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<OidcApplication> FindByPostLogoutRedirectUriAsync(string uri, CancellationToken cancellationToken) =>
        this.AllAsync(cancellationToken).Where(application => application.PostLogoutRedirectUris.Contains(uri, StringComparer.Ordinal));

    /// <inheritdoc />
    public IAsyncEnumerable<OidcApplication> FindByRedirectUriAsync(string uri, CancellationToken cancellationToken) =>
        this.AllAsync(cancellationToken).Where(application => application.RedirectUris.Contains(uri, StringComparer.Ordinal));

    /// <inheritdoc />
    public ValueTask<OidcApplication> InstantiateAsync(CancellationToken cancellationToken) => new(new OidcApplication());

    /// <inheritdoc />
    public ValueTask<string?> GetApplicationTypeAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.ApplicationType);

    /// <inheritdoc />
    public ValueTask<string?> GetClientIdAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.ClientId);

    /// <inheritdoc />
    public ValueTask<string?> GetClientSecretAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.ClientSecret);

    /// <inheritdoc />
    public ValueTask<string?> GetClientTypeAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.ClientType);

    /// <inheritdoc />
    public ValueTask<string?> GetConsentTypeAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.ConsentType);

    /// <inheritdoc />
    public ValueTask<string?> GetDisplayNameAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.DisplayName);

    /// <inheritdoc />
    public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(OidcApplication application, CancellationToken cancellationToken) =>
        new(ImmutableDictionary<CultureInfo, string>.Empty);

    /// <inheritdoc />
    public ValueTask<string?> GetIdAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.Id);

    /// <inheritdoc />
    public ValueTask<JsonWebKeySet?> GetJsonWebKeySetAsync(OidcApplication application, CancellationToken cancellationToken) => new((JsonWebKeySet?)null);

    /// <inheritdoc />
    public ValueTask<ImmutableArray<string>> GetPermissionsAsync(OidcApplication application, CancellationToken cancellationToken) => new([.. application.Permissions]);

    /// <inheritdoc />
    public ValueTask<ImmutableArray<string>> GetPostLogoutRedirectUrisAsync(OidcApplication application, CancellationToken cancellationToken) => new([.. application.PostLogoutRedirectUris]);

    /// <inheritdoc />
    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.Properties.ToImmutableDictionary());

    /// <inheritdoc />
    public ValueTask<ImmutableArray<string>> GetRedirectUrisAsync(OidcApplication application, CancellationToken cancellationToken) => new([.. application.RedirectUris]);

    /// <inheritdoc />
    public ValueTask<ImmutableArray<string>> GetRequirementsAsync(OidcApplication application, CancellationToken cancellationToken) => new([.. application.Requirements]);

    /// <inheritdoc />
    public ValueTask<ImmutableDictionary<string, string>> GetSettingsAsync(OidcApplication application, CancellationToken cancellationToken) => new(application.Settings.ToImmutableDictionary());

    /// <inheritdoc />
    public ValueTask SetApplicationTypeAsync(OidcApplication application, string? type, CancellationToken cancellationToken) => Set(() => application.ApplicationType = type);

    /// <inheritdoc />
    public ValueTask SetClientIdAsync(OidcApplication application, string? identifier, CancellationToken cancellationToken) => Set(() => application.ClientId = identifier);

    /// <inheritdoc />
    public ValueTask SetClientSecretAsync(OidcApplication application, string? secret, CancellationToken cancellationToken) => Set(() => application.ClientSecret = secret);

    /// <inheritdoc />
    public ValueTask SetClientTypeAsync(OidcApplication application, string? type, CancellationToken cancellationToken) => Set(() => application.ClientType = type);

    /// <inheritdoc />
    public ValueTask SetConsentTypeAsync(OidcApplication application, string? type, CancellationToken cancellationToken) => Set(() => application.ConsentType = type);

    /// <inheritdoc />
    public ValueTask SetDisplayNameAsync(OidcApplication application, string? name, CancellationToken cancellationToken) => Set(() => application.DisplayName = name);

    /// <inheritdoc />
    public ValueTask SetDisplayNamesAsync(OidcApplication application, ImmutableDictionary<CultureInfo, string> names, CancellationToken cancellationToken) =>
        names.IsEmpty ? default : throw new NotSupportedException("Localized client names are not supported.");

    /// <inheritdoc />
    public ValueTask SetJsonWebKeySetAsync(OidcApplication application, JsonWebKeySet? set, CancellationToken cancellationToken) =>
        set is null ? default : throw new NotSupportedException("Client key sets are not supported.");

    /// <inheritdoc />
    public ValueTask SetPermissionsAsync(OidcApplication application, ImmutableArray<string> permissions, CancellationToken cancellationToken) => Set(() => application.Permissions = [.. permissions]);

    /// <inheritdoc />
    public ValueTask SetPostLogoutRedirectUrisAsync(OidcApplication application, ImmutableArray<string> uris, CancellationToken cancellationToken) => Set(() => application.PostLogoutRedirectUris = [.. uris]);

    /// <inheritdoc />
    public ValueTask SetPropertiesAsync(OidcApplication application, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken) => Set(() => application.Properties = properties);

    /// <inheritdoc />
    public ValueTask SetRedirectUrisAsync(OidcApplication application, ImmutableArray<string> uris, CancellationToken cancellationToken) => Set(() => application.RedirectUris = [.. uris]);

    /// <inheritdoc />
    public ValueTask SetRequirementsAsync(OidcApplication application, ImmutableArray<string> requirements, CancellationToken cancellationToken) => Set(() => application.Requirements = [.. requirements]);

    /// <inheritdoc />
    public ValueTask SetSettingsAsync(OidcApplication application, ImmutableDictionary<string, string> settings, CancellationToken cancellationToken) => Set(() => application.Settings = settings);

    /// <inheritdoc />
    public ValueTask<long> CountAsync(CancellationToken cancellationToken) => throw Unsupported();

    /// <inheritdoc />
    public ValueTask<long> CountAsync<TResult>(Func<IQueryable<OidcApplication>, IQueryable<TResult>> query, CancellationToken cancellationToken) => throw Unsupported();

    /// <inheritdoc />
    public ValueTask<TResult?> GetAsync<TState, TResult>(Func<IQueryable<OidcApplication>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken) => throw Unsupported();

    /// <inheritdoc />
    public IAsyncEnumerable<OidcApplication> ListAsync(int? count, int? offset, CancellationToken cancellationToken) => throw Unsupported();

    /// <inheritdoc />
    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(Func<IQueryable<OidcApplication>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken) => throw Unsupported();

    // Scans the tenant's clients: a project registers a handful; index redirect URIs if that grows.
    private async IAsyncEnumerable<OidcApplication> AllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tenant = this.Tenant;
        await foreach (var document in store.QueryAsync(Collection, tenant, "/instanceId", tenant, cancellationToken).ConfigureAwait(false))
        {
            if (document.Document.Deserialize(OidcJson.Default.OidcApplication) is { ClientId: { } clientId } application && application.InstanceId == tenant && application.Id == clientId)
            {
                application.Version = document.Version;
                yield return application;
            }
        }
    }

    private static JsonElement Serialize(OidcApplication application) => JsonSerializer.SerializeToElement(application, OidcJson.Default.OidcApplication);

    internal static ValueTask Set(Action assign)
    {
        assign();
        return default;
    }

    internal static NotSupportedException Unsupported() => new("This OpenIddict store operation is not supported by the TerraBase stores.");
}

/// <summary>
/// OpenIddict authorizations on TerraBase, one per sign-in. Revoking one rejects every token issued under it, because
/// OpenIddict checks the authorization whenever it validates such a token.
/// </summary>
public sealed class OidcAuthorizationStore(AuthStore store, IHttpContextAccessor http, OidcRetention retention) : IOpenIddictAuthorizationStore<OidcAuthorization>
{
    private const string Collection = AuthStore.OidcAuthorizationCollection;

    // The tenant of the request being served (Tenancy); the platform's outside a tenant request.
    private string Tenant => Tenancy.Current(http.HttpContext);

    /// <inheritdoc />
    public async ValueTask CreateAsync(OidcAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        (authorization.Id, authorization.InstanceId) = (AuthStore.NewId(), this.Tenant);
        authorization.Version = (await store.CreateAsync(Collection, this.Tenant, authorization.Id, Serialize(authorization), cancellationToken).ConfigureAwait(false)).Version;
    }

    /// <inheritdoc />
    public async ValueTask UpdateAsync(OidcAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.Version = await store.ReplaceAsync(Collection, this.Tenant, authorization.Id!, Serialize(authorization), authorization.Version!, cancellationToken).ConfigureAwait(false)
            ?? throw new OpenIddictExceptions.ConcurrencyException("The authorization was changed by another request.");
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(OidcAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!await store.CompareAndDeleteAsync(Collection, this.Tenant, authorization.Id!, authorization.Version!, cancellationToken).ConfigureAwait(false))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The authorization was changed by another request.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<OidcAuthorization?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(identifier))
        {
            return null;
        }

        var document = await store.GetAsync(Collection, this.Tenant, identifier, cancellationToken).ConfigureAwait(false);
        return Read(document, this.Tenant) is { } authorization && authorization.Id == identifier ? authorization : null;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<OidcAuthorization> FindAsync(string? subject, string? client, string? status, string? type, ImmutableArray<string>? scopes, CancellationToken cancellationToken) =>
        (subject is not null ? this.FindAsync("/subject", subject, cancellationToken) : client is not null ? this.FindAsync("/applicationId", client, cancellationToken) : throw OidcApplicationStore.Unsupported())
            .Where(authorization => (subject is null || authorization.Subject == subject) && (client is null || authorization.ApplicationId == client)
                && (status is null || authorization.Status == status) && (type is null || authorization.Type == type)
                && (scopes is null || scopes.Value.All(authorization.Scopes.Contains)));

    /// <inheritdoc />
    public IAsyncEnumerable<OidcAuthorization> FindByApplicationIdAsync(string identifier, CancellationToken cancellationToken) => this.FindAsync("/applicationId", identifier, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<OidcAuthorization> FindBySubjectAsync(string subject, CancellationToken cancellationToken) => this.FindAsync("/subject", subject, cancellationToken);

    /// <inheritdoc />
    public ValueTask<long> RevokeAsync(string? subject, string? client, string? status, string? type, CancellationToken cancellationToken) =>
        this.RevokeAllAsync(this.FindAsync(subject, client, status, type, null, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken) => this.RevokeAllAsync(this.FindByApplicationIdAsync(identifier, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken) => this.RevokeAllAsync(this.FindBySubjectAsync(subject, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public ValueTask<OidcAuthorization> InstantiateAsync(CancellationToken cancellationToken) => new(new OidcAuthorization());

    /// <inheritdoc />
    public ValueTask<string?> GetApplicationIdAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.ApplicationId);

    /// <inheritdoc />
    public ValueTask<DateTimeOffset?> GetCreationDateAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.CreationDate);

    /// <inheritdoc />
    public ValueTask<string?> GetIdAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.Id);

    /// <inheritdoc />
    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.Properties.ToImmutableDictionary());

    /// <inheritdoc />
    public ValueTask<ImmutableArray<string>> GetScopesAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new([.. authorization.Scopes]);

    /// <inheritdoc />
    public ValueTask<string?> GetStatusAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.Status);

    /// <inheritdoc />
    public ValueTask<string?> GetSubjectAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.Subject);

    /// <inheritdoc />
    public ValueTask<string?> GetTypeAsync(OidcAuthorization authorization, CancellationToken cancellationToken) => new(authorization.Type);

    /// <inheritdoc />
    public ValueTask SetApplicationIdAsync(OidcAuthorization authorization, string? identifier, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.ApplicationId = identifier);

    /// <inheritdoc />
    public ValueTask SetCreationDateAsync(OidcAuthorization authorization, DateTimeOffset? date, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.CreationDate = date);

    /// <inheritdoc />
    public ValueTask SetPropertiesAsync(OidcAuthorization authorization, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.Properties = properties);

    /// <inheritdoc />
    public ValueTask SetScopesAsync(OidcAuthorization authorization, ImmutableArray<string> scopes, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.Scopes = [.. scopes]);

    /// <inheritdoc />
    public ValueTask SetStatusAsync(OidcAuthorization authorization, string? status, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.Status = status);

    /// <inheritdoc />
    public ValueTask SetSubjectAsync(OidcAuthorization authorization, string? subject, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.Subject = subject);

    /// <inheritdoc />
    public ValueTask SetTypeAsync(OidcAuthorization authorization, string? type, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => authorization.Type = type);

    /// <inheritdoc />
    public ValueTask<long> CountAsync(CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    /// <inheritdoc />
    public ValueTask<long> CountAsync<TResult>(Func<IQueryable<OidcAuthorization>, IQueryable<TResult>> query, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    /// <inheritdoc />
    public ValueTask<TResult?> GetAsync<TState, TResult>(Func<IQueryable<OidcAuthorization>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    /// <inheritdoc />
    public IAsyncEnumerable<OidcAuthorization> ListAsync(int? count, int? offset, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    /// <inheritdoc />
    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(Func<IQueryable<OidcAuthorization>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    /// <inheritdoc />
    public ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken) => retention.PruneAuthorizationsAsync(threshold, this.Tenant, cancellationToken);

    private async IAsyncEnumerable<OidcAuthorization> FindAsync(string field, string value, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var document in store.QueryAsync(Collection, this.Tenant, field, value, cancellationToken).ConfigureAwait(false))
        {
            if (Read(document, this.Tenant) is { } authorization)
            {
                yield return authorization;
            }
        }
    }

    private async ValueTask<long> RevokeAllAsync(IAsyncEnumerable<OidcAuthorization> authorizations, CancellationToken cancellationToken)
    {
        long revoked = 0;
        await foreach (var authorization in authorizations.ConfigureAwait(false))
        {
            // A lost write means another request changed the record; the next revocation sweep retries it.
            if (authorization.Status != Statuses.Revoked && await store.CompareAndPutAsync(Collection, this.Tenant, authorization.Id!,
                    Serialize(Revoked(authorization)), authorization.Version!, cancellationToken).ConfigureAwait(false))
            {
                revoked++;
            }
        }

        return revoked;

        static OidcAuthorization Revoked(OidcAuthorization authorization)
        {
            authorization.Status = Statuses.Revoked;
            return authorization;
        }
    }

    private static OidcAuthorization? Read(TerraBaseDocument? document, string tenant)
    {
        var authorization = document?.Document.Deserialize(OidcJson.Default.OidcAuthorization);
        if (authorization is not { Id: not null } || authorization.InstanceId != tenant)
        {
            return null;
        }

        authorization.Version = document!.Version;
        return authorization;
    }

    private static JsonElement Serialize(OidcAuthorization authorization) => JsonSerializer.SerializeToElement(authorization, OidcJson.Default.OidcAuthorization);
}

/// <summary>
/// OpenIddict token entries on TerraBase. Every status change (redeem, revoke, reject) is a conditional write on the
/// version this request read and throws <see cref="OpenIddictExceptions.ConcurrencyException"/> when another request
/// wrote first, so concurrent redemptions of one code or refresh token have exactly one winner. A reference token's
/// digest is reserved in <c>uniques</c> in the same transaction that stores it, which is how a handle is found.
/// </summary>
public sealed class OidcTokenStore(AuthStore store, IOpenIddictAuthorizationStore<OidcAuthorization> authorizations, IHttpContextAccessor http, OidcRetention retention) : IOpenIddictTokenStore<OidcToken>
{
    private const string Collection = AuthStore.OidcTokenCollection;

    // The tenant of the request being served (Tenancy); the platform's outside a tenant request.
    private string Tenant => Tenancy.Current(http.HttpContext);
    private const string ReferenceKind = "oidc_token_reference";

    /// <inheritdoc />
    public async ValueTask CreateAsync(OidcToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        (token.Id, token.InstanceId) = (AuthStore.NewId(), this.Tenant);
        // Every entry is bound when it is created, in the same remote-durable write, before its credential is returned.
        if (CredentialLineage.Issued(http.HttpContext) is { Count: > 0 } bindings)
        {
            token.Bindings = bindings;
        }

        if (token.ReferenceId is { } reference)
        {
            token.Version = await store.CreateClaimedAsync(Collection, this.Tenant, token.Id, Serialize(token), ReferenceKind, reference, cancellationToken).ConfigureAwait(false)
                ? (await store.GetAsync(Collection, this.Tenant, token.Id, cancellationToken).ConfigureAwait(false))?.Version
                : throw new InvalidOperationException("The token reference is already taken.");
            token.StoredReferenceId = reference;
            return;
        }

        token.Version = (await store.CreateAsync(Collection, this.Tenant, token.Id, Serialize(token), cancellationToken).ConfigureAwait(false)).Version;
    }

    /// <inheritdoc />
    public async ValueTask UpdateAsync(OidcToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        string? version;
        if (token.ReferenceId is { } reference && reference != token.StoredReferenceId)
        {
            if (token.StoredReferenceId is not null)
            {
                throw new NotSupportedException("A token reference never changes.");
            }

            version = await store.ReplaceClaimedAsync(Collection, this.Tenant, token.Id!, Serialize(token), token.Version!, ReferenceKind, reference, cancellationToken).ConfigureAwait(false);
            token.StoredReferenceId = version is null ? null : reference;
        }
        else
        {
            version = await store.ReplaceAsync(Collection, this.Tenant, token.Id!, Serialize(token), token.Version!, cancellationToken).ConfigureAwait(false);
        }

        token.Version = version ?? throw new OpenIddictExceptions.ConcurrencyException("The token was changed by another request.");
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(OidcToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!await store.DeleteWithClaimsAsync(Collection, this.Tenant, token.Id!, token.Version!, token.Id!, cancellationToken).ConfigureAwait(false))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The token was changed by another request.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<OidcToken?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(identifier))
        {
            return null;
        }

        var document = await store.GetAsync(Collection, this.Tenant, identifier, cancellationToken).ConfigureAwait(false);
        if (Read(document, this.Tenant) is not { } token || token.Id != identifier)
        {
            return null;
        }

        if (!await store.AreCredentialsCurrentAsync(this.Tenant, token.Bindings, cancellationToken).ConfigureAwait(false))
        {
            // Keep the payload resolvable so fresh OpenIddict lookups reach the explicit conflict notice.
            token.Status = Statuses.Revoked;
        }

        return token;
    }

    /// <summary>Reads persisted consumption evidence without changing the token's effective ancestry validity.</summary>
    internal async ValueTask<bool> IsPersistedRefreshRedeemedAsync(string identifier, string authorizationId, string clientId, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(identifier))
        {
            return false;
        }

        var document = await store.GetAsync(Collection, this.Tenant, identifier, cancellationToken).ConfigureAwait(false);
        return Read(document, this.Tenant) is { Status: Statuses.Redeemed, Type: TokenTypeIdentifiers.RefreshToken } token
            && token.Id == identifier && token.AuthorizationId == authorizationId && token.ApplicationId == clientId;
    }

    /// <inheritdoc />
    public async ValueTask<OidcToken?> FindByReferenceIdAsync(string identifier, CancellationToken cancellationToken) =>
        await store.FindUniqueOwnerAsync(this.Tenant, ReferenceKind, identifier, cancellationToken).ConfigureAwait(false) is { } owner
            && await this.FindByIdAsync(owner, cancellationToken).ConfigureAwait(false) is { } token && token.ReferenceId == identifier
            ? token
            : null;

    /// <inheritdoc />
    public IAsyncEnumerable<OidcToken> FindAsync(string? subject, string? client, string? status, string? type, CancellationToken cancellationToken) =>
        (subject is not null ? this.FindAsync("/subject", subject, cancellationToken) : client is not null ? this.FindAsync("/applicationId", client, cancellationToken) : throw OidcApplicationStore.Unsupported())
            .Where(token => (subject is null || token.Subject == subject) && (client is null || token.ApplicationId == client)
                && (status is null || token.Status == status) && (type is null || token.Type == type));

    /// <inheritdoc />
    public IAsyncEnumerable<OidcToken> FindByApplicationIdAsync(string identifier, CancellationToken cancellationToken) => this.FindAsync("/applicationId", identifier, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<OidcToken> FindByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken) => this.FindAsync("/authorizationId", identifier, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<OidcToken> FindBySubjectAsync(string subject, CancellationToken cancellationToken) => this.FindAsync("/subject", subject, cancellationToken);

    /// <inheritdoc />
    public ValueTask<long> RevokeAsync(string? subject, string? client, string? status, string? type, CancellationToken cancellationToken) =>
        this.RevokeAllAsync(this.FindAsync(subject, client, status, type, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken = default) => this.RevokeAllAsync(this.FindByApplicationIdAsync(identifier, cancellationToken), cancellationToken);

    /// <summary>
    /// Revokes the authorization itself rather than rewriting its token entries. OpenIddict checks the authorization
    /// whenever it validates one of its tokens, so this rejects every token issued under it, including one a concurrent
    /// request is still creating, and never breaks that request's conditional writes on its own entries. Returns 1 once
    /// the authorization is revoked, 0 when it does not exist.
    /// </summary>
    public async ValueTask<long> RevokeByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken)
    {
        for (var round = 0; round < 3; round++)
        {
            if (await authorizations.FindByIdAsync(identifier, cancellationToken).ConfigureAwait(false) is not { } authorization)
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
                await authorizations.UpdateAsync(authorization, cancellationToken).ConfigureAwait(false);
                return 1;
            }
            catch (OpenIddictExceptions.ConcurrencyException)
            {
                // Another request changed the authorization first: reread it.
            }
        }

        throw ApiException.Unavailable();
    }

    /// <inheritdoc />
    public ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken = default) => this.RevokeAllAsync(this.FindBySubjectAsync(subject, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public ValueTask<OidcToken> InstantiateAsync(CancellationToken cancellationToken) => new(new OidcToken());

    /// <inheritdoc />
    public ValueTask<string?> GetApplicationIdAsync(OidcToken token, CancellationToken cancellationToken) => new(token.ApplicationId);

    /// <inheritdoc />
    public ValueTask<string?> GetAuthorizationIdAsync(OidcToken token, CancellationToken cancellationToken) => new(token.AuthorizationId);

    /// <inheritdoc />
    public ValueTask<DateTimeOffset?> GetCreationDateAsync(OidcToken token, CancellationToken cancellationToken) => new(token.CreationDate);

    /// <inheritdoc />
    public ValueTask<DateTimeOffset?> GetExpirationDateAsync(OidcToken token, CancellationToken cancellationToken) => new(token.ExpirationDate);

    /// <inheritdoc />
    public ValueTask<string?> GetIdAsync(OidcToken token, CancellationToken cancellationToken) => new(token.Id);

    /// <inheritdoc />
    public ValueTask<string?> GetPayloadAsync(OidcToken token, CancellationToken cancellationToken) => new(token.Payload);

    /// <inheritdoc />
    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OidcToken token, CancellationToken cancellationToken) => new(token.Properties.ToImmutableDictionary());

    /// <inheritdoc />
    public ValueTask<DateTimeOffset?> GetRedemptionDateAsync(OidcToken token, CancellationToken cancellationToken) => new(token.RedemptionDate);

    /// <inheritdoc />
    public ValueTask<string?> GetReferenceIdAsync(OidcToken token, CancellationToken cancellationToken) => new(token.ReferenceId);

    /// <inheritdoc />
    public ValueTask<string?> GetStatusAsync(OidcToken token, CancellationToken cancellationToken) => new(token.Status);

    /// <inheritdoc />
    public ValueTask<string?> GetSubjectAsync(OidcToken token, CancellationToken cancellationToken) => new(token.Subject);

    /// <inheritdoc />
    public ValueTask<string?> GetTypeAsync(OidcToken token, CancellationToken cancellationToken) => new(token.Type);

    /// <inheritdoc />
    public ValueTask SetApplicationIdAsync(OidcToken token, string? identifier, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.ApplicationId = identifier);

    /// <inheritdoc />
    public ValueTask SetAuthorizationIdAsync(OidcToken token, string? identifier, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.AuthorizationId = identifier);

    /// <inheritdoc />
    public ValueTask SetCreationDateAsync(OidcToken token, DateTimeOffset? date, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.CreationDate = date);

    /// <inheritdoc />
    public ValueTask SetExpirationDateAsync(OidcToken token, DateTimeOffset? date, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.ExpirationDate = date);

    /// <inheritdoc />
    public ValueTask SetPayloadAsync(OidcToken token, string? payload, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.Payload = payload);

    /// <inheritdoc />
    public ValueTask SetPropertiesAsync(OidcToken token, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.Properties = properties);

    /// <inheritdoc />
    public ValueTask SetRedemptionDateAsync(OidcToken token, DateTimeOffset? date, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.RedemptionDate = date);

    /// <inheritdoc />
    public ValueTask SetReferenceIdAsync(OidcToken token, string? identifier, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.ReferenceId = identifier);

    /// <inheritdoc />
    public ValueTask SetStatusAsync(OidcToken token, string? status, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.Status = status);

    /// <inheritdoc />
    public ValueTask SetSubjectAsync(OidcToken token, string? subject, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.Subject = subject);

    /// <inheritdoc />
    public ValueTask SetTypeAsync(OidcToken token, string? type, CancellationToken cancellationToken) => OidcApplicationStore.Set(() => token.Type = type);

    /// <inheritdoc />
    public ValueTask<long> CountAsync(CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    /// <inheritdoc />
    public ValueTask<long> CountAsync<TResult>(Func<IQueryable<OidcToken>, IQueryable<TResult>> query, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    /// <inheritdoc />
    public ValueTask<TResult?> GetAsync<TState, TResult>(Func<IQueryable<OidcToken>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    /// <inheritdoc />
    public IAsyncEnumerable<OidcToken> ListAsync(int? count, int? offset, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    /// <inheritdoc />
    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(Func<IQueryable<OidcToken>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    /// <inheritdoc />
    public ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken) => retention.PruneTokensAsync(threshold, this.Tenant, cancellationToken);

    // ponytail: TerraBase query scan, used only for revocation; add an index when token volume makes it slow.
    private async IAsyncEnumerable<OidcToken> FindAsync(string field, string value, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var document in store.QueryAsync(Collection, this.Tenant, field, value, cancellationToken).ConfigureAwait(false))
        {
            if (Read(document, this.Tenant) is { } token)
            {
                yield return token;
            }
        }
    }

    private async ValueTask<long> RevokeAllAsync(IAsyncEnumerable<OidcToken> tokens, CancellationToken cancellationToken)
    {
        long revoked = 0;
        await foreach (var token in tokens.ConfigureAwait(false))
        {
            if (token.Status is Statuses.Revoked)
            {
                continue;
            }

            token.Status = Statuses.Revoked;
            // A lost write means another request changed the token; it is revoked again by the next sweep.
            if (await store.CompareAndPutAsync(Collection, this.Tenant, token.Id!, Serialize(token), token.Version!, cancellationToken).ConfigureAwait(false))
            {
                revoked++;
            }
        }

        return revoked;
    }

    private static OidcToken? Read(TerraBaseDocument? document, string tenant)
    {
        var token = document?.Document.Deserialize(OidcJson.Default.OidcToken);
        if (token is not { Id: not null } || token.InstanceId != tenant)
        {
            return null;
        }

        (token.Version, token.StoredReferenceId) = (document!.Version, token.ReferenceId);
        return token;
    }

    private static JsonElement Serialize(OidcToken token) => JsonSerializer.SerializeToElement(token, OidcJson.Default.OidcToken);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OidcApplication))]
[JsonSerializable(typeof(OidcAuthorization))]
[JsonSerializable(typeof(OidcToken))]
[JsonSerializable(typeof(OAuthErrorBody))]
internal sealed partial class OidcJson : JsonSerializerContext;
