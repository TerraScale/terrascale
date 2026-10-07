using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Store;
using TerraScale.DotNet.TerraBase;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Auth.Features.Oidc;

/// <summary>
/// OpenIddict clients on TerraBase, addressed by instance and client id. Every write is create-only or names the
/// version it read. LINQ queries, counting and paging are not supported: no OpenIddict server flow uses them.
/// </summary>
public sealed class OidcApplicationStore(AuthStore store, IServiceProvider services) : IOpenIddictApplicationStore<OidcApplication>
{
    private const string Collection = AuthStore.OidcApplicationCollection;

    /// <inheritdoc />
    public async ValueTask CreateAsync(OidcApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrEmpty(application.ClientId);
        (application.Id, application.InstanceId) = (application.ClientId, OidcIssuer.InstanceId);
        application.Version = (await store.CreateAsync(Collection, OidcIssuer.InstanceId, application.ClientId, Serialize(application), cancellationToken).ConfigureAwait(false)).Version;
    }

    /// <inheritdoc />
    public async ValueTask UpdateAsync(OidcApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Version = await store.ReplaceAsync(Collection, OidcIssuer.InstanceId, application.Id!, Serialize(application), application.Version!, cancellationToken).ConfigureAwait(false)
            ?? throw new OpenIddictExceptions.ConcurrencyException("The client was changed by another request.");
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(OidcApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!await store.CompareAndDeleteAsync(Collection, OidcIssuer.InstanceId, application.Id!, application.Version!, cancellationToken).ConfigureAwait(false))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The client was changed by another request.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<OidcApplication?> FindByClientIdAsync(string identifier, CancellationToken cancellationToken)
    {
        if (Platform.ApiKeys.IsKeyId(identifier))
        {
            // An API key (TS-598) is a client of its own collection; resolved lazily, since ApiKeys reads the store.
            var keys = services.GetRequiredService<Platform.ApiKeys>();
            return await keys.FindAsync(identifier, cancellationToken).ConfigureAwait(false) is { } key ? Platform.ApiKeyClients.Describe(key, keys.Digests(key)) : null;
        }

        var document = await store.GetAsync(Collection, OidcIssuer.InstanceId, identifier, cancellationToken).ConfigureAwait(false);
        var application = document?.Document.Deserialize(OidcJson.Default.OidcApplication);
        if (application is null || application.InstanceId != OidcIssuer.InstanceId || application.ClientId != identifier || application.Id != identifier)
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

    // ponytail: scans the instance's clients (one today); index redirect URIs when clients become self-service.
    private async IAsyncEnumerable<OidcApplication> AllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var document in store.QueryAsync(Collection, OidcIssuer.InstanceId, "/instanceId", OidcIssuer.InstanceId, cancellationToken).ConfigureAwait(false))
        {
            if (document.Document.Deserialize(OidcJson.Default.OidcApplication) is { InstanceId: OidcIssuer.InstanceId, ClientId: { } clientId } application && application.Id == clientId)
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
public sealed class OidcAuthorizationStore(AuthStore store) : IOpenIddictAuthorizationStore<OidcAuthorization>
{
    private const string Collection = AuthStore.OidcAuthorizationCollection;

    /// <inheritdoc />
    public async ValueTask CreateAsync(OidcAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        (authorization.Id, authorization.InstanceId) = (Guid.NewGuid().ToString("D"), OidcIssuer.InstanceId);
        authorization.Version = (await store.CreateAsync(Collection, OidcIssuer.InstanceId, authorization.Id, Serialize(authorization), cancellationToken).ConfigureAwait(false)).Version;
    }

    /// <inheritdoc />
    public async ValueTask UpdateAsync(OidcAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.Version = await store.ReplaceAsync(Collection, OidcIssuer.InstanceId, authorization.Id!, Serialize(authorization), authorization.Version!, cancellationToken).ConfigureAwait(false)
            ?? throw new OpenIddictExceptions.ConcurrencyException("The authorization was changed by another request.");
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(OidcAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!await store.CompareAndDeleteAsync(Collection, OidcIssuer.InstanceId, authorization.Id!, authorization.Version!, cancellationToken).ConfigureAwait(false))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The authorization was changed by another request.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<OidcAuthorization?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(Collection, OidcIssuer.InstanceId, identifier, cancellationToken).ConfigureAwait(false);
        return Read(document) is { } authorization && authorization.Id == identifier ? authorization : null;
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
    public ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    private async IAsyncEnumerable<OidcAuthorization> FindAsync(string field, string value, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var document in store.QueryAsync(Collection, OidcIssuer.InstanceId, field, value, cancellationToken).ConfigureAwait(false))
        {
            if (Read(document) is { } authorization)
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
            if (authorization.Status != Statuses.Revoked && await store.CompareAndPutAsync(Collection, OidcIssuer.InstanceId, authorization.Id!,
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

    private static OidcAuthorization? Read(TerraBaseDocument? document)
    {
        var authorization = document?.Document.Deserialize(OidcJson.Default.OidcAuthorization);
        if (authorization is not { InstanceId: OidcIssuer.InstanceId, Id: not null })
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
public sealed class OidcTokenStore(AuthStore store, IOpenIddictAuthorizationStore<OidcAuthorization> authorizations) : IOpenIddictTokenStore<OidcToken>
{
    private const string Collection = AuthStore.OidcTokenCollection;
    private const string ReferenceKind = "oidc_token_reference";

    /// <inheritdoc />
    public async ValueTask CreateAsync(OidcToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        (token.Id, token.InstanceId) = (Guid.NewGuid().ToString("D"), OidcIssuer.InstanceId);
        if (token.ReferenceId is { } reference)
        {
            token.Version = await store.CreateClaimedAsync(Collection, OidcIssuer.InstanceId, token.Id, Serialize(token), ReferenceKind, reference, cancellationToken).ConfigureAwait(false)
                ? (await store.GetAsync(Collection, OidcIssuer.InstanceId, token.Id, cancellationToken).ConfigureAwait(false))?.Version
                : throw new InvalidOperationException("The token reference is already taken.");
            token.StoredReferenceId = reference;
            return;
        }

        token.Version = (await store.CreateAsync(Collection, OidcIssuer.InstanceId, token.Id, Serialize(token), cancellationToken).ConfigureAwait(false)).Version;
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

            version = await store.ReplaceClaimedAsync(Collection, OidcIssuer.InstanceId, token.Id!, Serialize(token), token.Version!, ReferenceKind, reference, cancellationToken).ConfigureAwait(false);
            token.StoredReferenceId = version is null ? null : reference;
        }
        else
        {
            version = await store.ReplaceAsync(Collection, OidcIssuer.InstanceId, token.Id!, Serialize(token), token.Version!, cancellationToken).ConfigureAwait(false);
        }

        token.Version = version ?? throw new OpenIddictExceptions.ConcurrencyException("The token was changed by another request.");
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(OidcToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!await store.CompareAndDeleteAsync(Collection, OidcIssuer.InstanceId, token.Id!, token.Version!, cancellationToken).ConfigureAwait(false))
        {
            throw new OpenIddictExceptions.ConcurrencyException("The token was changed by another request.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<OidcToken?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(Collection, OidcIssuer.InstanceId, identifier, cancellationToken).ConfigureAwait(false);
        return Read(document) is { } token && token.Id == identifier ? token : null;
    }

    /// <inheritdoc />
    public async ValueTask<OidcToken?> FindByReferenceIdAsync(string identifier, CancellationToken cancellationToken) =>
        await store.FindUniqueOwnerAsync(OidcIssuer.InstanceId, ReferenceKind, identifier, cancellationToken).ConfigureAwait(false) is { } owner
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
    public ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken) => throw OidcApplicationStore.Unsupported();

    // ponytail: TerraBase query scan, used only for revocation; add an index when token volume makes it slow.
    private async IAsyncEnumerable<OidcToken> FindAsync(string field, string value, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var document in store.QueryAsync(Collection, OidcIssuer.InstanceId, field, value, cancellationToken).ConfigureAwait(false))
        {
            if (Read(document) is { } token)
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
            if (await store.CompareAndPutAsync(Collection, OidcIssuer.InstanceId, token.Id!, Serialize(token), token.Version!, cancellationToken).ConfigureAwait(false))
            {
                revoked++;
            }
        }

        return revoked;
    }

    private static OidcToken? Read(TerraBaseDocument? document)
    {
        var token = document?.Document.Deserialize(OidcJson.Default.OidcToken);
        if (token is not { InstanceId: OidcIssuer.InstanceId, Id: not null })
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
internal sealed partial class OidcJson : JsonSerializerContext;
