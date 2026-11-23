using Dapper;
using System.Text.Json;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Identity;

public sealed class Credentials(AuthData data)
{
    public const int MaxBindings = 64;

    public static readonly int MaxLineage = MaxBindings + (int)(TimeSpan.FromHours(12) / (OidcIssuer.TokenLifetime - TimeSpan.FromSeconds(30)));

    public const string SocialKind = "credential:social";

    private const string UserStateKind = "credential:user-state";

    private const string AuthorizationKind = "credential:authorization";

    public static CredentialBinding New(string kind, string key) => new("credential:" + kind, key, AuthIds.New());

    public static CredentialBinding UserState(string userId, string authStamp) => new(UserStateKind, userId, authStamp);

    public static CredentialBinding Authorization(string authorizationId) => new(AuthorizationKind, authorizationId, authorizationId);

    public static string SocialKey(LinkedProvider identity) => JsonSerializer.Serialize(identity, IdentityJson.Default.LinkedProvider);

    public static Change Claim(string instanceId, CredentialBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.Kind.StartsWith("credential:", StringComparison.Ordinal) || binding.Kind is UserStateKind or AuthorizationKind or SocialKind)
        {
            throw new ArgumentException("A credential claim needs a one-use credential kind.", nameof(binding));
        }

        return Change.Insert(new CredentialClaimRecord(instanceId, binding.Kind, binding.Key, binding.OwnerId));
    }

    public static Change Claim(string instanceId, string kind, string key, string ownerId) =>
        Change.Insert(new CredentialClaimRecord(instanceId, kind, key, ownerId));

    private async Task<IReadOnlyList<Versioned<CredentialClaimRecord>>> ClaimsAsync(string instanceId, string[] kinds, string[] keys, CancellationToken cancellationToken) =>
        Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<CredentialClaimRecord>(
            "SELECT * FROM credential_claims WHERE instance_id = @instanceId AND kind IN @kinds AND \"key\" IN @keys", new { instanceId, kinds, keys }), cancellationToken));

    private async Task<IReadOnlyList<Versioned<ProviderLinkRecord>>> LinksAsync(string instanceId, string[] subjects, CancellationToken cancellationToken) =>
        Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<ProviderLinkRecord>(
            "SELECT * FROM provider_links WHERE instance_id = @instanceId AND subject IN @subjects", new { instanceId, subjects }), cancellationToken));

    public Task<bool> IsClaimedAsync(string instanceId, CredentialBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return this.IsClaimedAsync(instanceId, binding.Kind, binding.Key, cancellationToken);
    }

    public Task<bool> IsClaimedAsync(string instanceId, string kind, string key, CancellationToken cancellationToken) =>
        data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM credential_claims WHERE instance_id = @instanceId AND kind = @kind AND \"key\" = @key)", new { instanceId, kind, key }),
            cancellationToken);

    public async Task<bool> AreCurrentAsync(string instanceId, IReadOnlyList<CredentialBinding> bindings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return bindings.Count <= MaxLineage && bindings.All(Wellformed)
            && await this.RequireAsync(instanceId, bindings, cancellationToken) is not null;
    }

    public async Task<IReadOnlyList<Change>?> RequireAsync(string instanceId, IReadOnlyList<CredentialBinding> bindings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count > MaxLineage || !bindings.All(Wellformed))
        {
            return null;
        }

        var distinct = bindings.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return [];
        }

        var userIds = distinct.Where(binding => binding.Kind == UserStateKind).Select(binding => binding.Key).Distinct(StringComparer.Ordinal).ToArray();
        var authorizationIds = distinct.Where(binding => binding.Kind == AuthorizationKind).Select(binding => binding.Key).Distinct(StringComparer.Ordinal).ToArray();
        var social = distinct.Where(binding => binding.Kind == SocialKind).ToArray();
        var claimed = distinct.Where(binding => binding.Kind is not (UserStateKind or AuthorizationKind or SocialKind)).ToArray();
        if (distinct.Any(binding => binding.Kind == AuthorizationKind && binding.OwnerId != binding.Key)
            || claimed.Any(binding => !binding.Kind.StartsWith("credential:", StringComparison.Ordinal)))
        {
            return null;
        }

        var identities = new List<(CredentialBinding Binding, LinkedProvider Identity)>();
        foreach (var binding in social)
        {
            if (Parse(binding.Key) is not { } identity)
            {
                return null;
            }

            identities.Add((binding, identity));
        }

        var usersRead = userIds.Length == 0 ? Task.FromResult<IReadOnlyList<UserRecord>>([])
            : data.ReadUsersAsync(connection => connection.QueryAsync<UserRecord>("SELECT * FROM users WHERE instance_id = @instanceId AND id IN @userIds", new { instanceId, userIds }),
                cancellationToken);
        var authorizationsRead = authorizationIds.Length == 0 ? Task.FromResult<IEnumerable<OidcAuthorization>>([])
            : data.ReadAsync(connection => connection.QueryAsync<OidcAuthorization>(
                "SELECT * FROM oidc_authorizations WHERE instance_id = @instanceId AND id IN @authorizationIds", new { instanceId, authorizationIds }), cancellationToken);
        var kinds = claimed.Select(binding => binding.Kind).Distinct(StringComparer.Ordinal).ToArray();
        var keys = claimed.Select(binding => binding.Key).Distinct(StringComparer.Ordinal).ToArray();
        var claimsRead = claimed.Length == 0 ? Task.FromResult<IReadOnlyList<Versioned<CredentialClaimRecord>>>([])
            : this.ClaimsAsync(instanceId, kinds, keys, cancellationToken);
        var subjects = identities.Select(item => item.Identity.Subject).Distinct(StringComparer.Ordinal).ToArray();
        var linksRead = identities.Count == 0 ? Task.FromResult<IReadOnlyList<Versioned<ProviderLinkRecord>>>([])
            : this.LinksAsync(instanceId, subjects, cancellationToken);
        await Task.WhenAll(usersRead, authorizationsRead, claimsRead, linksRead);

        var users = (await usersRead).ToDictionary(user => user.Id, StringComparer.Ordinal);
        var authorizations = (await authorizationsRead).ToDictionary(item => item.Id!, StringComparer.Ordinal);
        var claims = await claimsRead;
        var links = await linksRead;
        var changes = new List<Change>();
        foreach (var binding in distinct)
        {
            switch (binding.Kind)
            {
                case UserStateKind:
                    if (!users.TryGetValue(binding.Key, out var user) || user.AuthStamp != binding.OwnerId)
                    {
                        return null;
                    }

                    changes.Add(Change.Assert(user));
                    break;
                case AuthorizationKind:
                    if (!authorizations.TryGetValue(binding.Key, out var authorization) || authorization.Status != "valid")
                    {
                        return null;
                    }

                    changes.Add(Change.Assert(authorization));
                    break;
                case SocialKind:
                    var identity = identities.First(item => item.Binding == binding).Identity;
                    if (links.FirstOrDefault(link => link.Value.Identity == identity) is not { } held || held.Value.UserId != binding.OwnerId)
                    {
                        return null;
                    }

                    changes.Add(Change.Assert(held));
                    break;
                default:
                    if (claims.FirstOrDefault(claim => claim.Value.Kind == binding.Kind && claim.Value.Key == binding.Key) is not { } owned
                        || owned.Value.OwnerId != binding.OwnerId)
                    {
                        return null;
                    }

                    changes.Add(Change.Assert(owned));
                    break;
            }
        }

        return changes;
    }

    private static bool Wellformed(CredentialBinding? binding) =>
        binding is { Kind.Length: > 0, Key.Length: > 0, OwnerId.Length: > 0 };

    private static LinkedProvider? Parse(string key)
    {
        try
        {
            return JsonSerializer.Deserialize(key, IdentityJson.Default.LinkedProvider);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
