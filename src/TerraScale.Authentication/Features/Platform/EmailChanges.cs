using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>The requested sign-in address.</summary>
public sealed record EmailChangeRequest([property: JsonPropertyName("new_email")] string NewEmail);

/// <summary>The code the new address received.</summary>
public sealed record ConfirmEmailChangeRequest([property: JsonPropertyName("code")] string Code)
{
    /// <inheritdoc />
    public override string ToString() => nameof(ConfirmEmailChangeRequest);
}

/// <summary>A pending change: the new address and when its code stops working.</summary>
public sealed record EmailChangeBody(
    [property: JsonPropertyName("new_email")] string NewEmail,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

internal sealed record EmailChangeRecord(string InstanceId, string UserId, long AuthEpoch, string? AuthStamp, string NewEmail,
    string CodeHash, string CancelHash, DateTimeOffset ExpiresAt, int FailedAttempts, IReadOnlyList<CredentialBinding> Bindings);

/// <summary>
/// Sign-in email changes. A fresh <c>change_email</c> step-up proof starts one pending change per account: a single-use
/// code goes to the new address and a cancel link to the current one. Confirming the code at the same authentication
/// state swaps the unique email reservation and advances the epoch in one transaction, ending every session and token.
/// </summary>
public sealed class EmailChanges(AuthStore store, Users users, Sessions sessions, AttemptLimiter limiter, SignInMail mail, AuthOptions options, TimeProvider clock)
{
    /// <summary>How long a change's code and cancel link work.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Wrong codes a pending change survives.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Change requests per account, and per target address, per hour.</summary>
    public const int MaxRequests = 3;

    /// <summary>Change requests across the platform per hour, bounding the mail they send.</summary>
    public const int MaxGlobalRequests = 100;

    private readonly byte[] digestKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth email change digests"u8.ToArray());

    /// <summary>Starts a change after consuming the request's <c>change_email</c> proof; replaces any pending change.</summary>
    public async Task<EmailChangeBody> RequestAsync(UserRecord caller, HttpRequest request, string newEmail, StepUpProofs proofs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(proofs);
        if (Users.NormalizeEmail(newEmail) is not { } normalized || normalized == caller.Email)
        {
            throw new ApiException(400, "invalid_request", "new_email must be a valid address other than the current one.");
        }

        if (await users.FindByEmailAsync(caller.InstanceId, normalized, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw Taken();
        }

        if (!await limiter.TryAcquireAsync(caller.InstanceId, "email-change", caller.Id, MaxRequests, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false)
            || !await limiter.TryAcquireAsync(caller.InstanceId, "email-change-target", normalized, MaxRequests, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false)
            || !await limiter.TryAcquireAsync(caller.InstanceId, "email-change-global", caller.InstanceId, MaxGlobalRequests, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(429, "rate_limited", "Too many email change requests; retry later.");
        }

        using var codeSlot = mail.Reserve();
        using var noticeSlot = mail.Reserve();
        var verified = await proofs.ConsumeHeaderAsync(caller, request, "change_email", cancellationToken).ConfigureAwait(false);
        var code = Tokens.RandomDigits(6)!;
        var cancel = Tokens.RandomToken(43);
        var record = new EmailChangeRecord(caller.InstanceId, caller.Id, caller.AuthEpoch, caller.AuthStamp, normalized,
            this.Digest(caller, normalized, code), Tokens.Sha256Hex(cancel), clock.GetUtcNow() + Lifetime, 0, verified.StepUpBindings);
        var json = JsonSerializer.SerializeToElement(record, EmailChangeJson.Default.EmailChangeRecord);
        var existing = await store.GetAsync(AuthStore.EmailChangeCollection, caller.InstanceId, caller.Id, cancellationToken).ConfigureAwait(false);
        if (await store.TransactAsync([.. StepUpProofs.Requirements(verified), existing is null
            ? AuthStore.Create(AuthStore.EmailChangeCollection, caller.InstanceId, caller.Id, json)
            : AuthStore.Replace(AuthStore.EmailChangeCollection, caller.InstanceId, caller.Id, json, existing.Version)], cancellationToken).ConfigureAwait(false) is not null)
        {
            throw ApiException.Unavailable();
        }

        codeSlot.EnqueueEmail(AuthEmailKind.EmailChangeCode, new AuthEmailValues(normalized)
        {
            Code = code,
            ExpiresIn = Lifetime,
            NewEmail = normalized,
            Tenant = caller.InstanceId,
        });
        noticeSlot.EnqueueEmail(AuthEmailKind.EmailChangeNotice, new AuthEmailValues(caller.Email)
        {
            Link = new Uri(options.PublicOrigin, $"/ui/terrascale/email-change/cancel?user={caller.Id}&token={cancel}"),
            ExpiresIn = Lifetime,
            NewEmail = normalized,
            Tenant = caller.InstanceId,
        });
        return new EmailChangeBody(normalized, record.ExpiresAt);
    }

    /// <summary>Commits the caller's pending change with its code; every refusal looks alike except a taken address.</summary>
    public async Task ConfirmAsync(UserRecord caller, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        for (var round = 0; round < MaxAttempts + 2; round++)
        {
            if (await this.ReadAsync(caller.InstanceId, caller.Id, cancellationToken).ConfigureAwait(false) is not { } current
                || current.Record.AuthEpoch != caller.AuthEpoch || current.Record.AuthStamp != caller.AuthStamp
                || current.Record.FailedAttempts >= MaxAttempts)
            {
                throw Denied();
            }

            var record = current.Record;
            if (!Passcodes.WellFormed(code) || !Tokens.FixedTimeEquals(record.CodeHash, this.Digest(caller, record.NewEmail, code.Trim())))
            {
                var charged = JsonSerializer.SerializeToElement(record with { FailedAttempts = record.FailedAttempts + 1 }, EmailChangeJson.Default.EmailChangeRecord);
                if (await store.CompareAndPutAsync(AuthStore.EmailChangeCollection, caller.InstanceId, caller.Id, charged, current.Version, cancellationToken).ConfigureAwait(false))
                {
                    throw Denied();
                }

                continue;
            }

            if (record.Bindings.Count == 0 || !await store.AreCredentialsCurrentAsync(caller.InstanceId, record.Bindings, cancellationToken).ConfigureAwait(false))
            {
                throw Denied();
            }

            // Consuming the row, reserving the address and advancing the epoch commit together; a replay finds no row.
            // The user write itself is versioned, so its own state binding is not asserted separately.
            var lineage = record.Bindings.Where(binding => binding != AuthStore.UserStateBinding(caller.Id, record.AuthStamp ?? string.Empty)).ToArray();
            var changed = await users.ChangeEmailAsync(caller.InstanceId, caller.Id, record.AuthEpoch, record.AuthStamp, record.NewEmail,
                [AuthStore.Delete(AuthStore.EmailChangeCollection, caller.InstanceId, caller.Id, current.Version), .. AuthStore.CredentialRequirements(caller.InstanceId, lineage)],
                cancellationToken).ConfigureAwait(false);
            if (changed == true)
            {
                return;
            }

            throw changed == false ? Taken() : Denied();
        }

        throw Denied();
    }

    /// <summary>The current address's cancel link: ends the pending change by signing the account out everywhere.</summary>
    public async Task<bool> CancelAsync(string userId, string token, CancellationToken cancellationToken)
    {
        const string Instance = Oidc.OidcIssuer.InstanceId;
        if (!PlatformDirectory.IsId(userId) || token is not { Length: 43 }
            || await this.ReadAsync(Instance, userId, cancellationToken).ConfigureAwait(false) is not { } current
            || !Tokens.FixedTimeEquals(current.Record.CancelHash, Tokens.Sha256Hex(token)))
        {
            return false;
        }

        // The epoch advance alone voids the change; removing the row is cleanup.
        if (!await sessions.RevokeAllAsync(Instance, userId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await store.CompareAndDeleteAsync(AuthStore.EmailChangeCollection, Instance, userId, current.Version, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<(EmailChangeRecord Record, string Version)?> ReadAsync(string instanceId, string userId, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(AuthStore.EmailChangeCollection, instanceId, userId, cancellationToken).ConfigureAwait(false);
        return document?.Document.Deserialize(EmailChangeJson.Default.EmailChangeRecord) is { } record
            && record.InstanceId == instanceId && record.UserId == userId && record.ExpiresAt > clock.GetUtcNow()
                ? (record, document.Version) : null;
    }

    private string Digest(UserRecord caller, string newEmail, string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.digestKey, Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{caller.InstanceId.Length}:{caller.InstanceId}{caller.Id.Length}:{caller.Id}{newEmail.Length}:{newEmail}{code}"))));

    private static ApiException Denied() => new(403, "email_change_denied", "No matching pending email change; request a new one.");

    private static ApiException Taken() => new(409, "email_taken", "Another account already uses that email address.");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EmailChangeRecord))]
[JsonSerializable(typeof(EmailChangeRequest))]
[JsonSerializable(typeof(ConfirmEmailChangeRequest))]
[JsonSerializable(typeof(EmailChangeBody))]
internal sealed partial class EmailChangeJson : JsonSerializerContext;
