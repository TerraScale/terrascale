using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Identity;

public sealed class Users(AuthData data, Credentials credentials, WebhookOutbox outbox, TimeProvider clock, SignUpRestrictions restrictions)
{
    public static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        if (trimmed is not { Length: > 0 and <= 254 } || trimmed.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return null;
        }

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == trimmed.LastIndexOf('@') && trimmed[(at + 1)..].Split('.') is { Length: > 1 } labels && labels.All(label => label.Length > 0)
            ? AuthIds.NormalizeEmail(trimmed)
            : null;
    }

    public Task<UserRecord?> CreateAsync(string instanceId, string email, CancellationToken cancellationToken = default) =>
        this.CreateAsync(instanceId, email, null, cancellationToken);

    public async Task<UserRecord?> CreateAsync(string instanceId, string email, string? passwordHash, CancellationToken cancellationToken)
        => await this.CreateAccountAsync(instanceId, email, passwordHash, AuthIds.New(), cancellationToken).ConfigureAwait(false);

    internal (UserRecord User, Change[] Changes) NewAccount(string instanceId, string normalizedEmail, string? passwordHash, string userId, Change? lifecycleEvent = null)
    {
        var user = new UserRecord(userId, instanceId, normalizedEmail, 0, clock.GetUtcNow(), passwordHash) { AuthStamp = AuthIds.New() };
        return (user, lifecycleEvent is null ? [Change.Insert(user)] : [Change.Insert(user), lifecycleEvent]);
    }

    internal async Task<UserRecord?> CreateAccountAsync(string instanceId, string email, string? passwordHash, string userId, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEmail(email) ?? throw new ArgumentException("A valid email address is required.", nameof(email));
        if (await restrictions.AdmissionAsync(instanceId, normalized, true, cancellationToken).ConfigureAwait(false) is not { } admission)
        {
            return null;
        }

        var lifecycleEvent = await outbox.CreateAsync(instanceId, "user.signed_up", userId, normalized, null, cancellationToken).ConfigureAwait(false);
        var (user, changes) = this.NewAccount(instanceId, normalized, passwordHash, userId, lifecycleEvent);
        if (await data.SaveAsync([.. changes, .. admission], cancellationToken).ConfigureAwait(false))
        {
            return BindAuthenticationState(user);
        }

        return await this.FindByEmailAsync(instanceId, normalized, cancellationToken).ConfigureAwait(false) is not null ? null : throw ApiException.Unavailable();
    }

    public async Task<UserRecord?> FindByEmailAsync(string instanceId, string email, CancellationToken cancellationToken = default) =>
        NormalizeEmail(email) is { } normalized
            ? await this.ReadAsync(db => db.Users.Where(user => user.InstanceId == instanceId && user.Email == normalized), cancellationToken).ConfigureAwait(false)
            : null;

    public Task<UserRecord?> GetAsync(string instanceId, string userId, CancellationToken cancellationToken = default) =>
        TerraScaleIds.IsValid(userId)
            ? this.ReadAsync(db => db.Users.Where(user => user.InstanceId == instanceId && user.Id == userId), cancellationToken)
            : Task.FromResult<UserRecord?>(null);

    public async Task<UserRecord?> FindByProviderAsync(string tenant, LinkedProvider identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var linked = await this.ReadAsync(db => db.Users.Where(user => user.InstanceId == tenant && db.ProviderLinks.Any(link => link.UserId == user.Id && link.InstanceId == tenant
            && link.Provider == identity.Provider && link.Issuer == identity.Issuer && link.ClientId == identity.ClientId && link.Subject == identity.Subject)),
            cancellationToken).ConfigureAwait(false);
        return linked is { Disabled: false } && linked.LinkedProviders.Contains(identity) ? BindProvider(linked, identity) : null;
    }

    public async Task<UserRecord?> FromProviderAsync(string tenant, LinkedProvider identity, string verifiedEmail, bool allowCreation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Complete(identity))
        {
            return null;
        }

        for (var round = 0; round < 3; round++)
        {
            if (await this.LinkOwnerAsync(tenant, identity, cancellationToken).ConfigureAwait(false) is not null)
            {
                return await this.FindByProviderAsync(tenant, identity, cancellationToken).ConfigureAwait(false);
            }

            if (!allowCreation || NormalizeEmail(verifiedEmail) is not { } normalized
                || await this.FindByEmailAsync(tenant, normalized, cancellationToken).ConfigureAwait(false) is not null)
            {
                return null;
            }

            if (await restrictions.AdmissionAsync(tenant, normalized, true, cancellationToken).ConfigureAwait(false) is not { } admission)
            {
                return null;
            }

            var user = new UserRecord(AuthIds.New(), tenant, normalized, 0, clock.GetUtcNow()) { AuthStamp = AuthIds.New(), LinkedProviders = [identity] };
            List<Change> changes = [Change.Insert(user), Change.Insert(Link(user, identity)), .. admission];
            if (await outbox.CreateAsync(tenant, "user.signed_up", user.Id, normalized, null, cancellationToken).ConfigureAwait(false) is { } lifecycleEvent)
            {
                changes.Add(lifecycleEvent);
            }

            if (await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
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
            if (await this.GetAsync(caller.InstanceId, caller.Id, cancellationToken).ConfigureAwait(false) is not { } current
                || current.AuthStamp != caller.AuthStamp || current.AuthEpoch != caller.AuthEpoch || current.Disabled
                || !await credentials.AreCurrentAsync(caller.InstanceId, caller.AuthenticationBindings, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (current.LinkedProviders.Contains(identity))
            {
                return true;
            }

            List<Change> changes = [Change.Assert(current), Change.Insert(Link(current, identity))];
            if (await outbox.CreateAsync(caller.InstanceId, "user.provider_linked", caller.Id, current.Email, identity.Provider, cancellationToken).ConfigureAwait(false) is { } lifecycleEvent)
            {
                changes.Add(lifecycleEvent);
            }

            if (await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (await this.LinkOwnerAsync(caller.InstanceId, identity, cancellationToken).ConfigureAwait(false) is { } owner && owner != caller.Id)
            {
                return false;
            }
        }

        throw ApiException.Unavailable();
    }

    public async Task<bool> UnlinkProviderAsync(UserRecord caller, string provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await this.GetAsync(caller.InstanceId, caller.Id, cancellationToken).ConfigureAwait(false) is not { } current
            || current.AuthStamp != caller.AuthStamp || current.AuthEpoch != caller.AuthEpoch
            || !await credentials.AreCurrentAsync(caller.InstanceId, caller.AuthenticationBindings, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var links = await data.ReadAsync((db, token) => db.ProviderLinks
            .Where(link => link.InstanceId == caller.InstanceId && link.UserId == caller.Id && link.Provider == provider).WithVersion().ToListAsync(token), cancellationToken).ConfigureAwait(false);
        var updated = current with { AuthEpoch = checked(current.AuthEpoch + 1), AuthStamp = AuthIds.New() };
        List<Change> changes = [Change.Update(updated), .. links.Select(link => Change.Delete(link))];
        if (await outbox.CreateAsync(caller.InstanceId, "user.provider_unlinked", caller.Id, updated.Email, provider, cancellationToken).ConfigureAwait(false) is { } unlinked)
        {
            changes.Add(unlinked);
        }

        return await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetPasswordAsync(UserRecord caller, string passwordHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        for (var round = 0; round < 3; round++)
        {
            if (await this.GetAsync(caller.InstanceId, caller.Id, cancellationToken).ConfigureAwait(false) is not { } current
                || current.AuthEpoch != caller.AuthEpoch || current.AuthStamp != caller.AuthStamp
                || !await credentials.AreCurrentAsync(caller.InstanceId, caller.AuthenticationBindings, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (await data.SaveAsync(Change.Update(current with { PasswordHash = passwordHash, AuthEpoch = checked(caller.AuthEpoch + 1), AuthStamp = AuthIds.New() }),
                cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }

    internal async Task<bool?> ChangeEmailAsync(string instanceId, string userId, long authEpoch, string? authStamp, string newEmail,
        IReadOnlyList<Change> extra, CancellationToken cancellationToken)
    {
        if (await this.GetAsync(instanceId, userId, cancellationToken).ConfigureAwait(false) is not { } current
            || current.AuthEpoch != authEpoch || current.AuthStamp != authStamp || current.Disabled)
        {
            return null;
        }

        if (await this.FindByEmailAsync(instanceId, newEmail, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        var passcode = await data.ReadAsync((db, token) => db.Passcodes.Where(row => row.InstanceId == instanceId && row.Key == userId).WithVersion()
            .SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        List<Change> changes = [Change.Update(current with { Email = newEmail, AuthEpoch = checked(authEpoch + 1), AuthStamp = AuthIds.New() }), .. extra];
        if (await outbox.CreateAsync(instanceId, "user.updated", userId, newEmail, null, cancellationToken).ConfigureAwait(false) is { } updatedEvent)
        {
            changes.Add(updatedEvent);
        }

        if (passcode is not null)
        {
            changes.Add(Change.Delete(passcode));
        }

        if (await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return await this.FindByEmailAsync(instanceId, newEmail, cancellationToken).ConfigureAwait(false) is { } owner && owner.Id != userId ? false : null;
    }

    public async Task<UserRecord?> SetDisabledAsync(string instanceId, string userId, bool disabled, CancellationToken cancellationToken = default)
    {
        for (var round = 0; round < 3; round++)
        {
            if (await this.GetAsync(instanceId, userId, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return null;
            }

            if (current.Disabled == disabled)
            {
                return current;
            }

            var updated = current with { Disabled = disabled, AuthEpoch = checked(current.AuthEpoch + 1), AuthStamp = AuthIds.New() };
            List<Change> changes = [Change.Update(updated)];
            if (await outbox.CreateAsync(instanceId, disabled ? "user.disabled" : "user.enabled", userId, updated.Email, null, cancellationToken).ConfigureAwait(false) is { } lifecycleEvent)
            {
                changes.Add(lifecycleEvent);
            }

            if (await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
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
            if (await this.GetAsync(instanceId, userId, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return;
            }

            var signUpKey = Passcodes.SignUpKey + current.Email;
            var passcodes = await data.ReadAsync((db, token) => db.Passcodes
                .Where(row => row.InstanceId == instanceId && (row.Key == signUpKey || row.UserId == userId)).WithVersion().ToListAsync(token), cancellationToken).ConfigureAwait(false);
            List<Change> changes =
            [
                Change.Delete(current),
                DeletionJob.Enqueue(instanceId, "account", userId, clock, closureGeneration: closureGeneration),
                .. passcodes.Select(passcode => Change.Delete(passcode)),
            ];
            if (await outbox.CreateAsync(instanceId, "user.deleted", userId, current.Email, null, cancellationToken).ConfigureAwait(false) is { } deleted)
            {
                changes.Add(deleted);
            }

            if (await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
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

    private static ProviderLinkRecord Link(UserRecord user, LinkedProvider identity) =>
        new(user.InstanceId, user.Id, identity.Provider, identity.Issuer, identity.ClientId, identity.Subject);

    private static bool Complete(LinkedProvider identity) =>
        !string.IsNullOrWhiteSpace(identity.Provider) && !string.IsNullOrWhiteSpace(identity.Issuer)
        && !string.IsNullOrWhiteSpace(identity.ClientId) && !string.IsNullOrWhiteSpace(identity.Subject);

    private Task<string?> LinkOwnerAsync(string tenant, LinkedProvider identity, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.ProviderLinks.Where(link => link.InstanceId == tenant && link.Provider == identity.Provider && link.Issuer == identity.Issuer
            && link.ClientId == identity.ClientId && link.Subject == identity.Subject).Select(link => link.UserId).SingleOrDefaultAsync(token), cancellationToken);

    private async Task<UserRecord?> ReadAsync(Func<AuthDbContext, IQueryable<UserRecord>> users, CancellationToken cancellationToken)
    {
        var row = await data.ReadAsync((db, token) => users(db)
            .Select(user => new { User = user, Links = db.ProviderLinks.Where(link => link.UserId == user.Id).OrderBy(link => link.Provider).ThenBy(link => link.Subject).ToList() })
            .SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        return row is null ? null : BindAuthenticationState(row.User with { LinkedProviders = [.. row.Links.Select(link => link.Identity)] });
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LinkedProvider))]
[JsonSerializable(typeof(DateTimeOffset))]
internal sealed partial class IdentityJson : JsonSerializerContext;
