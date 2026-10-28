using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Store;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Identity;

/// <summary>User accounts. Callers authorize the instance before using these operations.</summary>
public sealed class Users(AuthStore store, TimeProvider clock)
{
    private const string EmailKind = "email";
    private const string ProviderKind = "credential:social";

    /// <summary>
    /// The canonical (trimmed, lowercased) email, or <see langword="null"/> when it is not a plausible address: at most
    /// 254 characters, no whitespace or control characters, one <c>@</c> after a non-empty local part, and a domain of
    /// at least two non-empty dot-separated labels.
    /// </summary>
    public static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        if (trimmed is not { Length: > 0 and <= 254 } || trimmed.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return null;
        }

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == trimmed.LastIndexOf('@') && trimmed[(at + 1)..].Split('.') is { Length: > 1 } labels && labels.All(label => label.Length > 0)
            ? AuthStore.NormalizeEmail(trimmed)
            : null;
    }

    /// <summary>
    /// Creates an account without a password. Callers must prove email possession first: tokens and directory exports assert email_verified.
    /// The email claim and the account are written in one transaction, so a
    /// claim never outlives a failed creation. Returns <see langword="null"/> when the email already has an account.
    /// </summary>
    public Task<UserRecord?> CreateAsync(string instanceId, string email, CancellationToken cancellationToken = default) =>
        this.CreateAsync(instanceId, email, null, cancellationToken);

    /// <summary>Creates an account with an Argon2id <paramref name="passwordHash"/>, or none; see the overload.</summary>
    public async Task<UserRecord?> CreateAsync(string instanceId, string email, string? passwordHash, CancellationToken cancellationToken)
        => await this.CreateAccountAsync(instanceId, email, passwordHash, AuthStore.NewId(), cancellationToken).ConfigureAwait(false);

    internal async Task<UserRecord?> CreateAccountAsync(string instanceId, string email, string? passwordHash, string userId, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEmail(email) ?? throw new ArgumentException("A valid email address is required.", nameof(email));
        var user = new UserRecord(userId, instanceId, normalized, 0, clock.GetUtcNow(), passwordHash) { AuthStamp = AuthStore.NewId() };
        var record = JsonSerializer.SerializeToElement(user, IdentityJson.Default.UserRecord);
        return await store.CreateClaimedAsync(AuthStore.UserCollection, instanceId, user.Id, record, EmailKind, normalized, cancellationToken).ConfigureAwait(false)
            ? BindAuthenticationState(user)
            : null;
    }

    /// <summary>The account owning <paramref name="email"/> in the instance, or <see langword="null"/>.</summary>
    public async Task<UserRecord?> FindByEmailAsync(string instanceId, string email, CancellationToken cancellationToken = default)
    {
        if (NormalizeEmail(email) is not { } normalized
            || await store.FindUniqueOwnerAsync(instanceId, EmailKind, normalized, cancellationToken).ConfigureAwait(false) is not { } owner)
        {
            return null;
        }

        var user = await this.GetAsync(instanceId, owner, cancellationToken).ConfigureAwait(false);
        return user?.Email == normalized ? user : null;
    }

    /// <summary>The account with <paramref name="userId"/> in the instance, or <see langword="null"/>.</summary>
    public async Task<UserRecord?> GetAsync(string instanceId, string userId, CancellationToken cancellationToken = default) =>
        (await this.GetVersionedAsync(instanceId, userId, cancellationToken).ConfigureAwait(false))?.User;

    /// <summary>Signs in a stable external subject, or links its verified email to one account atomically.</summary>
    public async Task<UserRecord?> FromProviderAsync(string tenant, LinkedProvider identity, string verifiedEmail, bool allowCreation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var key = ProviderKey(identity);
        for (var round = 0; round < 3; round++)
        {
            if (await store.FindUniqueOwnerAsync(tenant, ProviderKind, key, cancellationToken).ConfigureAwait(false) is { } owner)
            {
                var linked = await this.GetAsync(tenant, owner, cancellationToken).ConfigureAwait(false);
                return linked is { Disabled: false } && linked.LinkedProviders.Contains(identity) ? BindProvider(linked, identity) : null;
            }

            var user = await this.FindByEmailAsync(tenant, verifiedEmail, cancellationToken).ConfigureAwait(false);
            if (user is null && allowCreation)
            {
                user = await this.CreateAsync(tenant, verifiedEmail, cancellationToken).ConfigureAwait(false)
                    ?? await this.FindByEmailAsync(tenant, verifiedEmail, cancellationToken).ConfigureAwait(false);
            }

            if (user is null || user.Disabled || await this.GetVersionedAsync(tenant, user.Id, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return null;
            }

            if (current.User.Email != NormalizeEmail(verifiedEmail) || current.User.Disabled)
            {
                return null;
            }

            // One identity per provider and client. Never silently replace another linked subject.
            if (current.User.LinkedProviders.Any(item => item.Provider == identity.Provider && item.ClientId == identity.ClientId))
            {
                return null;
            }

            var updated = current.User with
            {
                LinkedProviders = [.. current.User.LinkedProviders, identity],
                AuthEpoch = checked(current.User.AuthEpoch + 1),
                AuthStamp = AuthStore.NewId(),
            };
            if (await store.TransactAsync([
                AuthStore.Claim(tenant, ProviderKind, key, user.Id),
                AuthStore.Replace(AuthStore.UserCollection, tenant, user.Id, JsonSerializer.SerializeToElement(updated, IdentityJson.Default.UserRecord), current.Version),
            ], cancellationToken).ConfigureAwait(false) is null)
            {
                return BindProvider(updated, identity);
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>Unlinks a provider at the reauthenticated account version and revokes all prior sessions.</summary>
    public async Task<bool> UnlinkProviderAsync(UserRecord caller, string provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await this.GetVersionedAsync(caller.InstanceId, caller.Id, cancellationToken).ConfigureAwait(false) is not { } current
            || current.User.AuthStamp != caller.AuthStamp || current.User.AuthEpoch != caller.AuthEpoch
            || !await store.AreCredentialsCurrentAsync(caller.InstanceId, caller.AuthenticationBindings, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var identities = current.User.LinkedProviders.Where(item => item.Provider == provider).ToArray();
        var updated = current.User with
        {
            LinkedProviders = [.. current.User.LinkedProviders.Where(item => item.Provider != provider)],
            AuthEpoch = checked(current.User.AuthEpoch + 1),
            AuthStamp = AuthStore.NewId(),
        };
        List<StoreOperation> operations = [AuthStore.Replace(AuthStore.UserCollection, caller.InstanceId, caller.Id,
            JsonSerializer.SerializeToElement(updated, IdentityJson.Default.UserRecord), current.Version)];
        foreach (var identity in identities)
        {
            if (await store.FindUniqueAsync(caller.InstanceId, ProviderKind, ProviderKey(identity), cancellationToken).ConfigureAwait(false) is not { } claim || claim.OwnerId != caller.Id)
            {
                return false;
            }

            operations.Add(AuthStore.Release(caller.InstanceId, ProviderKind, ProviderKey(identity), claim.Version));
        }

        return await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is null;
    }

    private static UserRecord BindProvider(UserRecord user, LinkedProvider identity) => BindAuthenticationState(user) with
    {
        AuthenticationBindings = [.. BindAuthenticationState(user).AuthenticationBindings, new CredentialBinding(ProviderKind, ProviderKey(identity), user.Id)],
    };

    private static string ProviderKey(LinkedProvider identity) => $"{identity.Provider}:{identity.ClientId}:{identity.Subject}";

    /// <summary>
    /// Sets the password of the account while its authentication state still matches <paramref name="caller"/>, and
    /// advances the epoch, which signs the user out everywhere. False when the account is gone or its epoch moved on
    /// (another reset or a sign-out everywhere won), so a code issued before that change never sets a password after it.
    /// </summary>
    public async Task<bool> SetPasswordAsync(UserRecord caller, string passwordHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        // Every write to a user advances its epoch, so a lost write always ends the loop on the epoch check.
        for (var round = 0; round < 3; round++)
        {
            if (await this.GetVersionedAsync(caller.InstanceId, caller.Id, cancellationToken).ConfigureAwait(false) is not { } current
                || current.User.AuthEpoch != caller.AuthEpoch || current.User.AuthStamp != caller.AuthStamp
                || !await store.AreCredentialsCurrentAsync(caller.InstanceId, caller.AuthenticationBindings, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var updated = JsonSerializer.SerializeToElement(current.User with { PasswordHash = passwordHash, AuthEpoch = checked(caller.AuthEpoch + 1), AuthStamp = AuthStore.NewId() }, IdentityJson.Default.UserRecord);
            if (await store.CompareAndPutAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id, updated, current.Version, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>
    /// Moves the account to <paramref name="newEmail"/> at the authentication state the change was requested in: releases
    /// the old address, reserves the new one and advances the epoch (signing out everywhere) in one transaction together
    /// with <paramref name="extra"/>. Null when the state moved on or the transaction lost; false when another account owns the address.
    /// </summary>
    internal async Task<bool?> ChangeEmailAsync(string instanceId, string userId, long authEpoch, string? authStamp, string newEmail,
        IReadOnlyList<StoreOperation> extra, CancellationToken cancellationToken)
    {
        if (await this.GetVersionedAsync(instanceId, userId, cancellationToken).ConfigureAwait(false) is not { } current
            || current.User.AuthEpoch != authEpoch || current.User.AuthStamp != authStamp || current.User.Disabled
            || await store.FindUniqueAsync(instanceId, EmailKind, current.User.Email, cancellationToken).ConfigureAwait(false) is not { } claim
            || claim.OwnerId != userId)
        {
            return null;
        }

        if (await store.FindUniqueOwnerAsync(instanceId, EmailKind, newEmail, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        var updated = current.User with { Email = newEmail, AuthEpoch = checked(authEpoch + 1), AuthStamp = AuthStore.NewId() };
        List<StoreOperation> operations =
        [
            AuthStore.Replace(AuthStore.UserCollection, instanceId, userId, JsonSerializer.SerializeToElement(updated, IdentityJson.Default.UserRecord), current.Version),
            AuthStore.Release(instanceId, EmailKind, current.User.Email, claim.Version),
            AuthStore.Claim(instanceId, EmailKind, newEmail, userId),
            .. extra,
        ];

        // The user's passcode row names the old address; it would refuse every later code for the new one.
        if (await store.GetAsync(AuthStore.PasscodeCollection, instanceId, userId, cancellationToken).ConfigureAwait(false) is { } passcode)
        {
            operations.Add(AuthStore.Delete(AuthStore.PasscodeCollection, instanceId, userId, passcode.Version));
        }

        if (await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is null)
        {
            return true;
        }

        return await store.FindUniqueOwnerAsync(instanceId, EmailKind, newEmail, cancellationToken).ConfigureAwait(false) is { } owner && owner != userId ? false : null;
    }

    /// <summary>
    /// Disables or enables the account. Either change advances the epoch, so disabling ends every session and token at
    /// once; <see cref="Sessions"/> refuses to open or resolve a session of a disabled account. Null when it is gone.
    /// </summary>
    public async Task<UserRecord?> SetDisabledAsync(string instanceId, string userId, bool disabled, CancellationToken cancellationToken = default)
    {
        for (var round = 0; round < 3; round++)
        {
            if (await this.GetVersionedAsync(instanceId, userId, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return null;
            }

            if (current.User.Disabled == disabled)
            {
                return current.User;
            }

            var updated = current.User with { Disabled = disabled, AuthEpoch = checked(current.User.AuthEpoch + 1), AuthStamp = AuthStore.NewId() };
            if (await store.CompareAndPutAsync(AuthStore.UserCollection, instanceId, userId, JsonSerializer.SerializeToElement(updated, IdentityJson.Default.UserRecord), current.Version, cancellationToken).ConfigureAwait(false))
            {
                return BindAuthenticationState(updated);
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>
    /// Deletes the account and releases its email in one transaction, so the address can sign up again. Every check
    /// reads the user, so its sessions and tokens stop working at once.
    /// </summary>
    public Task DeleteAsync(string instanceId, string userId, CancellationToken cancellationToken = default) =>
        this.DeleteAsync(instanceId, userId, null, cancellationToken);

    internal async Task DeleteAsync(string instanceId, string userId, string? closureGeneration, CancellationToken cancellationToken)
    {
        for (var round = 0; round < 3; round++)
        {
            if (await this.GetVersionedAsync(instanceId, userId, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return;
            }

            List<StoreOperation> operations =
            [
                AuthStore.Delete(AuthStore.UserCollection, instanceId, userId, current.Version),
                DeletionJob.Enqueue(instanceId, "account", userId, clock, closureGeneration: closureGeneration),
            ];
            if (await store.FindUniqueAsync(instanceId, EmailKind, current.User.Email, cancellationToken).ConfigureAwait(false) is { } claim && claim.OwnerId == userId)
            {
                operations.Add(AuthStore.Release(instanceId, EmailKind, current.User.Email, claim.Version));
            }

            if (await store.GetAsync(AuthStore.PasscodeCollection, instanceId, Passcodes.SignUpKey + current.User.Email, cancellationToken).ConfigureAwait(false) is { } pending)
            {
                operations.Add(AuthStore.Delete(AuthStore.PasscodeCollection, instanceId, Passcodes.SignUpKey + current.User.Email, pending.Version));
            }

            if (await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is null)
            {
                return;
            }
        }

        throw ApiException.Unavailable();
    }

    internal async Task<(UserRecord User, string Version)?> GetVersionedAsync(string instanceId, string userId, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(userId))
        {
            return null;
        }

        var document = await store.GetAsync(AuthStore.UserCollection, instanceId, userId, cancellationToken).ConfigureAwait(false);
        var user = document?.Document.Deserialize(IdentityJson.Default.UserRecord);
        return user is not null && user.InstanceId == instanceId && user.Id == userId
            && await store.FindUniqueOwnerAsync(instanceId, EmailKind, user.Email, cancellationToken).ConfigureAwait(false) == userId
            ? (BindAuthenticationState(user), document!.Version) : null;
    }

    internal static UserRecord BindAuthenticationState(UserRecord user) => user with
    {
        AuthenticationBindings = user.AuthStamp is { } stamp ? [AuthStore.UserStateBinding(user.Id, stamp)] : [],
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UserRecord))]
[JsonSerializable(typeof(SessionRecord))]
[JsonSerializable(typeof(PasscodeRecord))]
[JsonSerializable(typeof(DateTimeOffset))]
internal sealed partial class IdentityJson : JsonSerializerContext;
