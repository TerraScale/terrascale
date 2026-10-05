using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Store;

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
    /// Creates an account. The email claim and the account are written in one transaction, so a claim never
    /// outlives a failed creation. Returns <see langword="null"/> when the email already has an account.
    /// </summary>
    public async Task<UserRecord?> CreateAsync(string instanceId, string email, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeEmail(email) ?? throw new ArgumentException("A valid email address is required.", nameof(email));
        var user = new UserRecord(Guid.NewGuid().ToString("D"), instanceId, normalized, 0, clock.GetUtcNow());
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

    internal async Task<(UserRecord User, string Version)?> GetVersionedAsync(string instanceId, string userId, CancellationToken cancellationToken)
    {
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
