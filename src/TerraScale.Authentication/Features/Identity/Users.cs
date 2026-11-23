using System.Globalization;
using System.Text.Json.Serialization;
using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Impersonation;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Identity;

public sealed partial class Users(AuthData data, Credentials credentials, WebhookOutbox outbox, TimeProvider clock, SignUpRestrictions restrictions)
{
    public const string GuestEmailDomain = "@anonymous.invalid";

    public static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        if (trimmed is not { Length: > 0 and <= 254 } || trimmed.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return null;
        }

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != trimmed.LastIndexOf('@') || NormalizeDomain(trimmed[(at + 1)..]) is not { } domain)
        {
            return null;
        }

        var normalized = AuthIds.NormalizeEmail(trimmed[..at] + "@" + domain);
        return normalized.Length <= 254 ? normalized : null;
    }

    internal static string? NormalizeDomain(string domain)
    {
        try
        {
            var ascii = new IdnMapping().GetAscii(domain).ToLowerInvariant();
            return Uri.CheckHostName(ascii) == UriHostNameType.Dns && ascii.Split('.') is { Length: > 1 } labels && labels.All(label => label.Length > 0)
                && labels[^1] != "invalid" ? ascii : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public Task<UserRecord?> CreateAsync(string instanceId, string email, CancellationToken cancellationToken = default) =>
        this.CreateAsync(instanceId, email, null, cancellationToken);

    public async Task<UserRecord?> CreateAsync(string instanceId, string email, string? passwordHash, CancellationToken cancellationToken)
        => await this.CreateAccountAsync(instanceId, email, passwordHash, AuthIds.New(), cancellationToken);

    internal (UserRecord User, Change[] Changes) NewAccount(string instanceId, string normalizedEmail, string? passwordHash, string userId, Change? lifecycleEvent = null, string? username = null)
    {
        var user = new UserRecord(userId, instanceId, normalizedEmail, 0, clock.GetUtcNow(), passwordHash) { AuthStamp = AuthIds.New(), Username = username };
        return (user, lifecycleEvent is null ? [Change.Insert(user)] : [Change.Insert(user), lifecycleEvent]);
    }

    public async Task<UserRecord> CreateGuestAsync(string instanceId, CancellationToken cancellationToken)
    {
        var id = AuthIds.New();
        var guest = new UserRecord(id, instanceId, id.ToLowerInvariant() + GuestEmailDomain, 0, clock.GetUtcNow())
        {
            AuthStamp = AuthIds.New(),
            EmailVerified = false,
            IsAnonymous = true,
        };
        return await data.SaveAsync(Change.Insert(guest), cancellationToken) ? BindAuthenticationState(guest) : throw ApiException.Unavailable();
    }

    internal async Task<UserRecord?> CreateAccountAsync(string instanceId, string email, string? passwordHash, string userId, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEmail(email) ?? throw new ArgumentException("A valid email address is required.", nameof(email));
        if (await restrictions.AdmissionAsync(instanceId, normalized, true, cancellationToken) is not { } admission)
        {
            return null;
        }

        var lifecycleEvent = await outbox.CreateAsync(instanceId, "user.signed_up", userId, normalized, null, cancellationToken);
        var (user, changes) = this.NewAccount(instanceId, normalized, passwordHash, userId, lifecycleEvent);
        if (await data.SaveAsync([.. changes, .. admission], cancellationToken))
        {
            return BindAuthenticationState(user);
        }

        return await this.FindByEmailAsync(instanceId, normalized, cancellationToken) is not null ? null : throw ApiException.Unavailable();
    }

    public async Task<UserRecord?> FindByEmailAsync(string instanceId, string email, CancellationToken cancellationToken = default) =>
        NormalizeEmail(email) is { } normalized
            ? await this.WithLinksAsync(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
                "SELECT * FROM users WHERE instance_id = @instanceId AND email = @normalized", new { instanceId, normalized }), cancellationToken), cancellationToken)
            : null;

    public static string? NormalizeUsername(string? username) => username is { Length: >= 3 and <= 32 }
        && username.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.')
        && username.ToLowerInvariant() is not ("admin" or "administrator" or "root" or "support" or "security" or "help" or "terrascale" or "system" or "owner" or "staff")
        ? username.ToLowerInvariant() : null;

    public Task<UserRecord?> FindByUsernameAsync(string instanceId, string username, CancellationToken cancellationToken) =>
        NormalizeUsername(username) is { } normalized
            ? this.ReadUsernameAsync(instanceId, normalized, cancellationToken)
            : Task.FromResult<UserRecord?>(null);

    public Task<UserRecord?> GetAsync(string instanceId, string userId, CancellationToken cancellationToken = default) =>
        TerraScaleIds.IsValid(userId)
            ? this.ReadIdAsync(instanceId, userId, cancellationToken)
            : Task.FromResult<UserRecord?>(null);

    public async Task<UserRecord?> FindByProviderAsync(string tenant, LinkedProvider identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var linked = await this.LinkOwnerAsync(tenant, identity, cancellationToken) is { } owner ? await this.ReadIdAsync(tenant, owner, cancellationToken) : null;
        return linked is { Disabled: false } && linked.LinkedProviders.Contains(identity) ? BindProvider(linked, identity) : null;
    }

    public async Task<UserRecord?> FromProviderAsync(string tenant, LinkedProvider identity, string verifiedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Complete(identity))
        {
            return null;
        }

        for (var round = 0; round < 3; round++)
        {
            if (await this.LinkOwnerAsync(tenant, identity, cancellationToken) is not null)
            {
                return await this.FindByProviderAsync(tenant, identity, cancellationToken);
            }

            if (NormalizeEmail(verifiedEmail) is not { } normalized
                || await this.FindByEmailAsync(tenant, normalized, cancellationToken) is not null)
            {
                return null;
            }

            if (await restrictions.AdmissionAsync(tenant, normalized, true, cancellationToken) is not { } admission)
            {
                return null;
            }

            var user = new UserRecord(AuthIds.New(), tenant, normalized, 0, clock.GetUtcNow()) { AuthStamp = AuthIds.New(), LinkedProviders = [identity] };
            List<Change> changes = [Change.Insert(user), Change.Insert(Link(user, identity)), .. admission];
            if (await outbox.CreateAsync(tenant, "user.signed_up", user.Id, normalized, null, cancellationToken) is { } lifecycleEvent)
            {
                changes.Add(lifecycleEvent);
            }

            if (await data.SaveAsync(changes, cancellationToken))
            {
                return BindProvider(user, identity);
            }
        }

        throw ApiException.Unavailable();
    }

    public async Task<bool?> LinkProviderAsync(UserRecord caller, LinkedProvider identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(identity);
        if (!Complete(identity))
        {
            return null;
        }

        for (var round = 0; round < 3; round++)
        {
            if (await this.GetAsync(caller.InstanceId, caller.Id, cancellationToken) is not { } current
                || current.AuthStamp != caller.AuthStamp || current.AuthEpoch != caller.AuthEpoch || current.Disabled
                || !await credentials.AreCurrentAsync(caller.InstanceId, caller.AuthenticationBindings, cancellationToken))
            {
                return null;
            }

            if (current.LinkedProviders.Contains(identity))
            {
                return true;
            }

            List<Change> changes = [Change.Assert(current), Change.Insert(Link(current, identity))];
            if (await outbox.CreateAsync(caller.InstanceId, "user.provider_linked", caller.Id, current.Email, identity.Provider, cancellationToken) is { } lifecycleEvent)
            {
                changes.Add(lifecycleEvent);
            }

            if (await outbox.CreateAsync(caller.InstanceId, "user.updated", caller.Id, current.Email, identity.Provider, cancellationToken) is { } updatedEvent)
            {
                changes.Add(updatedEvent);
            }

            if (await data.SaveAsync(changes, cancellationToken))
            {
                return true;
            }

            if (await this.LinkOwnerAsync(caller.InstanceId, identity, cancellationToken) is { } owner && owner != caller.Id)
            {
                return false;
            }
        }

        throw ApiException.Unavailable();
    }

    public async Task<bool> UnlinkProviderAsync(UserRecord caller, string provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await this.GetAsync(caller.InstanceId, caller.Id, cancellationToken) is not { } current
            || current.AuthStamp != caller.AuthStamp || current.AuthEpoch != caller.AuthEpoch
            || !await credentials.AreCurrentAsync(caller.InstanceId, caller.AuthenticationBindings, cancellationToken))
        {
            return false;
        }

        var (instance, user) = (caller.InstanceId, caller.Id);
        var links = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<ProviderLinkRecord>(
            "SELECT * FROM provider_links WHERE instance_id = @instance AND user_id = @user AND provider = @provider", new { instance, user, provider }), cancellationToken));
        var updated = current with { AuthEpoch = checked(current.AuthEpoch + 1), AuthStamp = AuthIds.New() };
        List<Change> changes = [Change.Update(updated), .. links.Select(link => Change.Delete(link))];
        if (await outbox.CreateAsync(caller.InstanceId, "user.provider_unlinked", caller.Id, updated.Email, provider, cancellationToken) is { } unlinked)
        {
            changes.Add(unlinked);
        }

        return await data.SaveAsync(changes, cancellationToken);
    }

    internal async Task<bool> SetUsernameAsync(UserRecord caller, string username, Change session, CancellationToken cancellationToken)
    {
        if (NormalizeUsername(username) is not { } normalized
            || await this.GetAsync(caller.InstanceId, caller.Id, cancellationToken) is not { Disabled: false } current
            || current.AuthEpoch != caller.AuthEpoch || current.AuthStamp != caller.AuthStamp)
        {
            return false;
        }

        return await data.SaveAsync([Change.Update(current with { Username = normalized }), session], cancellationToken);
    }

    public async Task<bool> SetPasswordAsync(UserRecord caller, string passwordHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        for (var round = 0; round < 3; round++)
        {
            if (await this.GetAsync(caller.InstanceId, caller.Id, cancellationToken) is not { } current
                || current.AuthEpoch != caller.AuthEpoch || current.AuthStamp != caller.AuthStamp
                || !await credentials.AreCurrentAsync(caller.InstanceId, caller.AuthenticationBindings, cancellationToken))
            {
                return false;
            }

            if (await data.SaveAsync(Change.Update(current with { PasswordHash = passwordHash, AuthEpoch = checked(caller.AuthEpoch + 1), AuthStamp = AuthIds.New() }),
                cancellationToken))
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }

    internal async Task<bool?> ChangeEmailAsync(string instanceId, string userId, long authEpoch, string? authStamp, string newEmail,
        IReadOnlyList<Change> extra, CancellationToken cancellationToken)
    {
        if (await this.GetAsync(instanceId, userId, cancellationToken) is not { } current
            || current.AuthEpoch != authEpoch || current.AuthStamp != authStamp || current.Disabled)
        {
            return null;
        }

        if (await this.FindByEmailAsync(instanceId, newEmail, cancellationToken) is not null)
        {
            return false;
        }

        var passcode = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<PasscodeRecord>(
            "SELECT * FROM passcodes WHERE instance_id = @instanceId AND \"key\" = @userId", new { instanceId, userId }), cancellationToken));
        List<Change> changes = [Change.Update(current with { Email = newEmail, EmailVerified = true, AuthEpoch = checked(authEpoch + 1), AuthStamp = AuthIds.New() }), .. extra];
        if (await outbox.CreateAsync(instanceId, "user.updated", userId, newEmail, null, cancellationToken) is { } updatedEvent)
        {
            changes.Add(updatedEvent);
        }

        if (passcode is not null)
        {
            changes.Add(Change.Delete(passcode));
        }

        if (await data.SaveAsync(changes, cancellationToken))
        {
            return true;
        }

        return await this.FindByEmailAsync(instanceId, newEmail, cancellationToken) is { } owner && owner.Id != userId ? false : null;
    }

    public async Task<UserRecord?> SetDisabledAsync(string instanceId, string userId, bool disabled, CancellationToken cancellationToken = default)
    {
        for (var round = 0; round < 3; round++)
        {
            if (await this.GetAsync(instanceId, userId, cancellationToken) is not { } current)
            {
                return null;
            }

            if (current.Disabled == disabled)
            {
                return current;
            }

            var updated = current with { Disabled = disabled, AuthEpoch = checked(current.AuthEpoch + 1), AuthStamp = AuthIds.New() };
            List<Change> changes = [Change.Update(updated)];
            if (await outbox.CreateAsync(instanceId, disabled ? "user.disabled" : "user.enabled", userId, updated.Email, null, cancellationToken) is { } lifecycleEvent)
            {
                changes.Add(lifecycleEvent);
            }

            if (await data.SaveAsync(changes, cancellationToken))
            {
                return BindAuthenticationState(updated);
            }
        }

        throw ApiException.Unavailable();
    }

    public Task DeleteAsync(string instanceId, string userId, CancellationToken cancellationToken = default) =>
        this.DeleteAsync(instanceId, userId, null, cancellationToken);

    internal async Task DeleteAsync(string instanceId, string userId, string? closureGeneration, CancellationToken cancellationToken)
    {
        for (var round = 0; round < 3; round++)
        {
            if (await this.GetAsync(instanceId, userId, cancellationToken) is not { } current)
            {
                return;
            }

            var impersonations = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<ImpersonationRecord>(
                "SELECT * FROM impersonations WHERE ended_at IS NULL AND (user_id = @userId OR actor_id = @userId)", new { userId }), cancellationToken));
            var signUpKey = Passcodes.SignUpKey + current.Email;
            var passcodes = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<PasscodeRecord>(
                "SELECT * FROM passcodes WHERE instance_id = @instanceId AND (\"key\" = @signUpKey OR user_id = @userId)", new { instanceId, signUpKey, userId }),
                cancellationToken));
            List<Change> changes =
            [
                Change.Delete(current),
                DeletionJob.Enqueue(instanceId, "account", userId, clock, closureGeneration: closureGeneration),
                .. passcodes.Select(passcode => Change.Delete(passcode)),
            ];
            foreach (var impersonation in impersonations)
            {
                changes.Add(Change.Delete(impersonation));
                changes.AddRange(ImpersonationEndings.Events(impersonation.Value,
                    impersonation.Value.UserId == userId ? "target_deleted" : "admin_deleted", impersonation.Value.ActorId, outbox, clock));
            }

            if (await this.RemoveOrganizationMembershipsAsync(instanceId, userId, cancellationToken) is not { } memberships)
            {
                continue;
            }

            changes.AddRange(memberships);
            if (await outbox.CreateAsync(instanceId, "user.deleted", userId, current.Email, null, cancellationToken) is { } deleted)
            {
                changes.Add(deleted);
            }

            if (await data.SaveAsync(changes, cancellationToken))
            {
                return;
            }
        }

        throw ApiException.Unavailable();
    }

    internal static UserRecord BindAuthenticationState(UserRecord user) => user with
    {
        AuthenticationBindings = user.AuthStamp is { } stamp ? [Credentials.UserState(user.Id, stamp)] : [],
    };

    private static UserRecord BindProvider(UserRecord user, LinkedProvider identity) => BindAuthenticationState(user) with
    {
        AuthenticationBindings = [.. BindAuthenticationState(user).AuthenticationBindings, new CredentialBinding(Credentials.SocialKind, Credentials.SocialKey(identity), user.Id)],
    };

    internal static ProviderLinkRecord Link(UserRecord user, LinkedProvider identity) =>
        new(user.InstanceId, user.Id, identity.Provider, identity.Issuer, identity.ClientId, identity.Subject);

    private static bool Complete(LinkedProvider identity) =>
        !string.IsNullOrWhiteSpace(identity.Provider) && !string.IsNullOrWhiteSpace(identity.Issuer)
        && !string.IsNullOrWhiteSpace(identity.ClientId) && !string.IsNullOrWhiteSpace(identity.Subject);

    private Task<string?> LinkOwnerAsync(string tenant, LinkedProvider identity, CancellationToken cancellationToken)
    {
        var (provider, issuer, client, subject) = (identity.Provider, identity.Issuer, identity.ClientId, identity.Subject);
        return data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<string>(
            "SELECT user_id FROM provider_links WHERE instance_id = @tenant AND provider = @provider AND issuer = @issuer AND client_id = @client AND subject = @subject",
            new { tenant, provider, issuer, client, subject }), cancellationToken);
    }

    private async Task<UserRecord?> ReadIdAsync(string instanceId, string userId, CancellationToken cancellationToken) =>
        await this.WithLinksAsync(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
            "SELECT * FROM users WHERE instance_id = @instanceId AND id = @userId", new { instanceId, userId }), cancellationToken), cancellationToken);

    private async Task<UserRecord?> ReadUsernameAsync(string instanceId, string username, CancellationToken cancellationToken) =>
        await this.WithLinksAsync(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
            "SELECT * FROM users WHERE instance_id = @instanceId AND username = @username", new { instanceId, username }), cancellationToken), cancellationToken);

    private async Task<UserRecord?> WithLinksAsync(UserRecord? user, CancellationToken cancellationToken)
    {
        if (user is null)
        {
            return null;
        }

        var id = user.Id;
        var links = await data.ReadAsync(connection => connection.QueryAsync<ProviderLinkRecord>(
            "SELECT * FROM provider_links WHERE user_id = @id ORDER BY provider, subject", new { id }), cancellationToken);
        return BindAuthenticationState(user with { LinkedProviders = [.. links.Select(link => link.Identity)] });
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LinkedProvider))]
[JsonSerializable(typeof(DateTimeOffset))]
internal sealed partial class IdentityJson : JsonSerializerContext;
