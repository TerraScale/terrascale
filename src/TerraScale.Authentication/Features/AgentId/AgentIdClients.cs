using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;
using ZLogger;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.AgentId;

public sealed record AgentIdClientRecord(string Id, string OwnerUserId, string Name, IReadOnlyList<string> RedirectUris, string SecretDigest, DateTimeOffset CreatedAt,
    string? PreviousSecretDigest = null, DateTimeOffset? PreviousSecretExpiresAt = null)
{
    public const int DigestLength = 64;

    public const int MaxRedirectUriLength = 2048;
}

public sealed record CreateAgentIdClientRequest(string? Name, IReadOnlyList<string>? RedirectUris);

public sealed record RotateAgentIdClientRequest(int? GraceSeconds);

public sealed record AgentIdClientBody(string ClientId, string Name, IReadOnlyList<string> RedirectUris, DateTimeOffset CreatedAt,
    DateTimeOffset? PreviousSecretExpiresAt = null, string? ClientSecret = null);

public sealed record AgentIdClientPageBody(IReadOnlyList<AgentIdClientBody> Items);

public sealed record AgentIdClientSecretBody(string ClientId, string ClientSecret, DateTimeOffset? PreviousSecretExpiresAt);

public sealed class AgentIdClients(AuthData data, TimeProvider clock, ILogger<AgentIdClients> logger)
{
    public const int MaxPerAccount = 10;

    public const int MaxRedirectUris = 10;

    public static readonly TimeSpan DefaultGrace = TimeSpan.FromHours(1);

    public static readonly TimeSpan MaxGrace = TimeSpan.FromDays(1);

    public async Task<AgentIdClientBody> CreateAsync(UserRecord owner, string name, IReadOnlyList<string> redirectUris, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var user = await data.ReadAsync((db, token) => db.Users.Where(row => row.InstanceId == owner.InstanceId && row.Id == owner.Id).WithVersion().SingleOrDefaultAsync(token),
            cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        var count = await data.ReadAsync((db, token) => db.AgentIdClients.CountAsync(row => row.OwnerUserId == owner.Id, token), cancellationToken).ConfigureAwait(false);
        if (count >= MaxPerAccount)
        {
            throw new ApiException(409, "client_limit", $"An account can have at most {MaxPerAccount} Sign in as agent clients; delete one first.");
        }

        var secret = Tokens.RandomToken(48);
        var client = new AgentIdClientRecord(AuthIds.New(), owner.Id, name, redirectUris, Tokens.Sha256Hex(secret), clock.GetUtcNow());
        if (!await data.SaveAsync([Change.Insert(client), Change.Update(user, user.Value)], cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(409, "version_conflict", "The account changed concurrently; try again.");
        }

        logger.ZLogInformation($"Sign in as agent client {client.Id} created by {owner.Id}");
        return Body(client) with { ClientSecret = secret };
    }

    public async Task<AgentIdClientPageBody> ListAsync(string ownerId, CancellationToken cancellationToken) =>
        new([.. (await data.ReadAsync((db, token) => db.AgentIdClients.Where(row => row.OwnerUserId == ownerId).OrderBy(row => row.Id).ToListAsync(token), cancellationToken)
            .ConfigureAwait(false)).Select(Body)]);

    public async Task<AgentIdClientSecretBody> RotateAsync(string ownerId, string clientId, TimeSpan grace, CancellationToken cancellationToken)
    {
        var client = await this.OwnedAsync(ownerId, clientId, cancellationToken).ConfigureAwait(false);
        var secret = Tokens.RandomToken(48);
        var expires = grace > TimeSpan.Zero ? clock.GetUtcNow() + grace : (DateTimeOffset?)null;
        var rotated = client.Value with
        {
            SecretDigest = Tokens.Sha256Hex(secret),
            PreviousSecretDigest = expires is null ? null : client.Value.SecretDigest,
            PreviousSecretExpiresAt = expires,
        };
        if (!await data.SaveAsync(Change.Update(client, rotated), cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(409, "version_conflict", "The client changed concurrently; try again.");
        }

        logger.ZLogInformation($"Sign in as agent client {clientId} secret rotated by {ownerId}");
        return new(clientId, secret, expires);
    }

    public async Task DeleteAsync(string ownerId, string clientId, CancellationToken cancellationToken)
    {
        var client = await this.OwnedAsync(ownerId, clientId, cancellationToken).ConfigureAwait(false);
        if (!await data.SaveAsync(Change.Delete(client), cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(409, "version_conflict", "The client changed concurrently; try again.");
        }

        logger.ZLogInformation($"Sign in as agent client {clientId} deleted by {ownerId}");
    }

    public async Task<OidcApplication?> FindAsync(string clientId, CancellationToken cancellationToken)
    {
        var (client, owner) = await data.ReadAsync(async (db, token) =>
        {
            var found = await db.AgentIdClients.SingleOrDefaultAsync(row => row.Id == clientId, token).ConfigureAwait(false);
            return (found, found is null ? null : await db.Users.SingleOrDefaultAsync(row => row.InstanceId == PlatformTenant.Id && row.Id == found.OwnerUserId, token).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        return client is not null && owner is { Disabled: false } ? Describe(client, clock.GetUtcNow()) : null;
    }

    public static UserRecord Owner(UserRecord caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.ActingAgentId is null && caller.InstanceId == PlatformTenant.Id
            ? caller
            : throw new ApiException(403, "owner_required", "Only the account itself, not an agent acting for it, can do this.");
    }

    public static string? RedirectUri(string? value) =>
        value is { Length: > 0 and <= AgentIdClientRecord.MaxRedirectUriLength } && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
            && string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal)
            ? value
            : null;

    private static OidcApplication Describe(AgentIdClientRecord client, DateTimeOffset now) => new()
    {
        Id = client.Id,
        ClientId = client.Id,
        InstanceId = AgentIdIssuer.Slug,
        ClientType = ClientTypes.Confidential,
        ApplicationType = ApplicationTypes.Web,
        ConsentType = ConsentTypes.Implicit,
        DisplayName = client.Name,
        ClientSecret = ApiKeyApplicationManager.DigestPrefix + client.SecretDigest,
        PreviousSecretHash = client.PreviousSecretDigest is { } previous && client.PreviousSecretExpiresAt > now ? ApiKeyApplicationManager.DigestPrefix + previous : null,
        PreviousSecretExpiresAt = client.PreviousSecretExpiresAt,
        RedirectUris = client.RedirectUris,
        Permissions =
        [
            Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.GrantTypes.AuthorizationCode, Permissions.ResponseTypes.Code,
            Permissions.Prefixes.Scope + Scopes.Profile, Permissions.Prefixes.Scope + AgentIdIssuer.OwnerEmailScope,
        ],
        Version = string.Create(CultureInfo.InvariantCulture, $"agentid-client:{client.Id}"),
    };

    private async Task<Versioned<AgentIdClientRecord>> OwnedAsync(string ownerId, string clientId, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => db.AgentIdClients.Where(row => row.Id == clientId && row.OwnerUserId == ownerId).WithVersion().SingleOrDefaultAsync(token),
            cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();

    private static AgentIdClientBody Body(AgentIdClientRecord client) =>
        new(client.Id, client.Name, client.RedirectUris, client.CreatedAt, client.PreviousSecretDigest is null ? null : client.PreviousSecretExpiresAt);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(CreateAgentIdClientRequest))]
[JsonSerializable(typeof(RotateAgentIdClientRequest))]
[JsonSerializable(typeof(AgentIdClientBody))]
[JsonSerializable(typeof(AgentIdClientPageBody))]
[JsonSerializable(typeof(AgentIdClientSecretBody))]
internal sealed partial class AgentIdClientJson : JsonSerializerContext;
