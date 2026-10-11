using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Store;
using TerraScale.DotNet;

namespace TerraScale.Auth.Features.Identity;

/// <summary>User accounts. Callers authorize the instance before using these operations.</summary>
public sealed class Users(AuthStore store, TimeProvider clock)
{
    private const string EmailKind = "email";

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
    /// Creates an account without a password. The email claim and the account are written in one transaction, so a
    /// claim never outlives a failed creation. Returns <see langword="null"/> when the email already has an account.
    /// </summary>
    public Task<UserRecord?> CreateAsync(string instanceId, string email, CancellationToken cancellationToken = default) =>
        this.CreateAsync(instanceId, email, null, cancellationToken);

    /// <summary>Creates an account with an Argon2id <paramref name="passwordHash"/>, or none; see the overload.</summary>
    public async Task<UserRecord?> CreateAsync(string instanceId, string email, string? passwordHash, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEmail(email) ?? throw new ArgumentException("A valid email address is required.", nameof(email));
        var user = new UserRecord(AuthStore.NewId(), instanceId, normalized, 0, clock.GetUtcNow(), passwordHash);
        var record = JsonSerializer.SerializeToElement(user, IdentityJson.Default.UserRecord);
        return await store.CreateClaimedAsync(AuthStore.UserCollection, instanceId, user.Id, record, EmailKind, normalized, cancellationToken).ConfigureAwait(false)
            ? user
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

    /// <summary>
    /// Sets the password of the account while its authentication epoch is still <paramref name="authEpoch"/>, and
    /// advances the epoch, which signs the user out everywhere. False when the account is gone or its epoch moved on
    /// (another reset or a sign-out everywhere won), so a code issued before that change never sets a password after it.
    /// </summary>
    public async Task<bool> SetPasswordAsync(string instanceId, string userId, long authEpoch, string passwordHash, CancellationToken cancellationToken = default)
    {
        // Every write to a user advances its epoch, so a lost write always ends the loop on the epoch check.
        for (var round = 0; round < 3; round++)
        {
            if (await this.GetVersionedAsync(instanceId, userId, cancellationToken).ConfigureAwait(false) is not { } current || current.User.AuthEpoch != authEpoch)
            {
                return false;
            }

            var updated = JsonSerializer.SerializeToElement(current.User with { PasswordHash = passwordHash, AuthEpoch = authEpoch + 1 }, IdentityJson.Default.UserRecord);
            if (await store.CompareAndPutAsync(AuthStore.UserCollection, instanceId, userId, updated, current.Version, cancellationToken).ConfigureAwait(false))
            {
                return true;
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
        return user is not null && user.InstanceId == instanceId && user.Id == userId ? (user, document!.Version) : null;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UserRecord))]
[JsonSerializable(typeof(SessionRecord))]
[JsonSerializable(typeof(PasscodeRecord))]
[JsonSerializable(typeof(DateTimeOffset))]
internal sealed partial class IdentityJson : JsonSerializerContext;
