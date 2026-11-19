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

public sealed record AgentIdRegistration(Versioned<AgentIdClientRecord> Client, Versioned<UserRecord> Owner, OidcApplication Application)
{
    public IReadOnlyList<Change> Asserts => [Change.Assert(this.Client), Change.Assert(this.Owner)];

    public readonly record struct RequestCopy(string ClientId);
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

    internal async Task<AgentIdClientBody> CreateAsync(PreparedStepUp proof, string name, IReadOnlyList<string> redirectUris, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var owner = proof.Caller;
        using var home = HomeReads.Begin();
        var user = await data.ReadAsync((db, token) => db.Users.Where(row => row.InstanceId == owner.InstanceId && row.Id == owner.Id).WithVersion().SingleOrDefaultAsync(token),
            cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        var count = await data.ReadAsync((db, token) => db.AgentIdClients.CountAsync(row => row.OwnerUserId == owner.Id, token), cancellationToken).ConfigureAwait(false);
        if (count >= MaxPerAccount)
        {
            throw new ApiException(409, "client_limit", $"An account can have at most {MaxPerAccount} Sign in as agent clients; delete one first.");
        }

        var secret = Tokens.RandomToken(48);
        var client = new AgentIdClientRecord(AuthIds.New(), owner.Id, name, redirectUris, Tokens.Sha256Hex(secret), clock.GetUtcNow());
        await this.SaveAsync(proof, [Change.Insert(client), Change.Update(user, user.Value)], cancellationToken).ConfigureAwait(false);
        logger.ZLogInformation($"Sign in as agent client {client.Id} created by {owner.Id}");
        return Body(client) with { ClientSecret = secret };
    }

    public async Task<AgentIdClientPageBody> ListAsync(string ownerId, CancellationToken cancellationToken)
    {
        using var home = HomeReads.Begin();
        return new([.. (await data.ReadAsync((db, token) => db.AgentIdClients.Where(row => row.OwnerUserId == ownerId).OrderBy(row => row.Id).ToListAsync(token), cancellationToken)
            .ConfigureAwait(false)).Select(Body)]);
    }

    internal async Task<AgentIdClientSecretBody> RotateAsync(PreparedStepUp proof, string clientId, TimeSpan grace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var client = await this.OwnedAsync(proof.Caller.Id, clientId, cancellationToken).ConfigureAwait(false);
        var secret = Tokens.RandomToken(48);
        var expires = grace > TimeSpan.Zero ? clock.GetUtcNow() + grace : (DateTimeOffset?)null;
        var rotated = client.Value with
        {
            SecretDigest = Tokens.Sha256Hex(secret),
            PreviousSecretDigest = expires is null ? null : client.Value.SecretDigest,
            PreviousSecretExpiresAt = expires,
        };
        await this.SaveAsync(proof, [Change.Update(client, rotated)], cancellationToken).ConfigureAwait(false);
        logger.ZLogInformation($"Sign in as agent client {clientId} secret rotated by {proof.Caller.Id}");
        return new(clientId, secret, expires);
    }

    internal async Task DeleteAsync(PreparedStepUp proof, string clientId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var client = await this.OwnedAsync(proof.Caller.Id, clientId, cancellationToken).ConfigureAwait(false);
        await this.SaveAsync(proof, [Change.Delete(client)], cancellationToken).ConfigureAwait(false);
        logger.ZLogInformation($"Sign in as agent client {clientId} deleted by {proof.Caller.Id}");
    }

    public async Task<AgentIdRegistration?> FindAsync(string clientId, CancellationToken cancellationToken)
    {
        using var home = HomeReads.Begin();
        var (client, owner) = await data.ReadAsync(async (db, token) =>
        {
            var found = await db.AgentIdClients.Where(row => row.Id == clientId).WithVersion().SingleOrDefaultAsync(token).ConfigureAwait(false);
            return (found, found is null ? null : await db.Users.Where(row => row.InstanceId == PlatformTenant.Id && row.Id == found.Value.OwnerUserId).WithVersion()
                .SingleOrDefaultAsync(token).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        return client is not null && owner is { Value.Disabled: false } ? new(client, owner, Describe(client.Value, clock.GetUtcNow())) : null;
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

    private async Task<Versioned<AgentIdClientRecord>> OwnedAsync(string ownerId, string clientId, CancellationToken cancellationToken)
    {
        using var home = HomeReads.Begin();
        return await data.ReadAsync((db, token) => db.AgentIdClients.Where(row => row.Id == clientId && row.OwnerUserId == ownerId).WithVersion().SingleOrDefaultAsync(token),
            cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
    }

    private async Task SaveAsync(PreparedStepUp proof, IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        if (proof.Record.ExpiresAt <= clock.GetUtcNow())
        {
            throw StepUpProofs.Required();
        }

        if (!await data.SaveAsync([.. proof.Operations, .. changes], cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(409, "version_conflict", "The client, the account or its step-up proof changed; try again.");
        }
    }

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
