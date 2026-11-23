using Dapper;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Platform;

public sealed record EmailChangeRequest([property: JsonPropertyName("new_email")] string NewEmail);

public sealed record ConfirmEmailChangeRequest([property: JsonPropertyName("code")] string Code)
{
    public override string ToString() => nameof(ConfirmEmailChangeRequest);
}

public sealed record EmailChangeBody(
    [property: JsonPropertyName("new_email")] string NewEmail,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public readonly union EmailChangeRequestResult(EmailChangeBody, Validation, Conflict, QuotaExceeded, Forbidden);

public readonly union EmailChangeConfirmResult(Done, Forbidden, Conflict);

public sealed record EmailChangeRecord(string InstanceId, string UserId, long AuthEpoch, string? AuthStamp, string NewEmail,
    string CodeHash, string CancelHash, DateTimeOffset ExpiresAt, int FailedAttempts, IReadOnlyList<CredentialBinding> Bindings)
{
    public string? Version { get; init; }
}

public sealed class EmailChanges(AuthData data, Credentials credentials, Users users, Sessions sessions, AttemptLimiter limiter, SignInMail mail, AuthOptions options, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public const int MaxAttempts = 5;

    public const int MaxRequests = 3;

    public const int MaxGlobalRequests = 100;

    private readonly byte[] digestKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth email change digests"u8.ToArray());

    public async Task<EmailChangeRequestResult> RequestAsync(UserRecord caller, HttpRequest request, string newEmail, StepUpProofs proofs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(proofs);
        if (Users.NormalizeEmail(newEmail) is not { } normalized || normalized == caller.Email)
        {
            return new Validation("new_email must be a valid address other than the current one.");
        }

        if (await users.FindByEmailAsync(caller.InstanceId, normalized, cancellationToken) is not null)
        {
            return EmailTaken();
        }

        if (!await limiter.TryAcquireAsync(caller.InstanceId, "email-change", caller.Id, MaxRequests, TimeSpan.FromHours(1), cancellationToken)
            || !await limiter.TryAcquireAsync(caller.InstanceId, "email-change-target", normalized, MaxRequests, TimeSpan.FromHours(1), cancellationToken)
            || !await limiter.TryAcquireAsync(caller.InstanceId, "email-change-global", caller.InstanceId, MaxGlobalRequests, TimeSpan.FromHours(1), cancellationToken))
        {
            return new QuotaExceeded("Too many email change requests; retry later.", "rate_limited");
        }

        using var codeSlot = mail.Reserve();
        using var noticeSlot = mail.Reserve();
        return await proofs.ConsumeAsync(caller, StepUpProofs.Proof(request),
            "change_email", "ts-auth", null, cancellationToken) switch
        {
            StepUpConsumption consumption => await this.IssueAsync(caller, consumption.Caller, normalized, proofs, codeSlot, noticeSlot, cancellationToken),
            Forbidden failure => failure,
        };
    }

    private async Task<EmailChangeRequestResult> IssueAsync(UserRecord caller, UserRecord verified, string normalized, StepUpProofs proofs, SignInMail.Reservation codeSlot, SignInMail.Reservation noticeSlot, CancellationToken cancellationToken)
    {
        var code = Tokens.RandomDigits(6)!;
        var cancel = Tokens.RandomToken(43);
        var record = new EmailChangeRecord(caller.InstanceId, caller.Id, caller.AuthEpoch, caller.AuthStamp, normalized,
            this.Digest(caller, normalized, code), Tokens.Sha256Hex(cancel), clock.GetUtcNow() + Lifetime, 0, verified.StepUpBindings);
        var existing = await this.ReadStoredAsync(caller.Id, cancellationToken);
        if (await proofs.RequirementsAsync(verified, cancellationToken) is not { } requirements
            || !await data.SaveAsync([.. requirements, existing is null ? Change.Insert(record) : Change.Update(existing, record)], cancellationToken))
        {
            throw ApiException.Unavailable();
        }

        await codeSlot.EnqueueEmailAsync(AuthEmailKind.EmailChangeCode, new AuthEmailValues(normalized)
        {
            Code = code,
            ExpiresIn = Lifetime,
            NewEmail = normalized,
            Tenant = caller.InstanceId,
        });
        await noticeSlot.EnqueueEmailAsync(AuthEmailKind.EmailChangeNotice, new AuthEmailValues(caller.Email)
        {
            Link = new Uri(options.PublicOrigin, $"/ui/terrascale/email-change/cancel?user={caller.Id}&token={cancel}"),
            ExpiresIn = Lifetime,
            NewEmail = normalized,
            Tenant = caller.InstanceId,
        });
        return new EmailChangeBody(normalized, record.ExpiresAt);
    }

    public async Task<EmailChangeConfirmResult> ConfirmAsync(UserRecord caller, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        for (var round = 0; round < MaxAttempts + 2; round++)
        {
            if (await this.ReadAsync(caller.InstanceId, caller.Id, cancellationToken) is not { } current
                || current.Record.AuthEpoch != caller.AuthEpoch || current.Record.AuthStamp != caller.AuthStamp
                || current.Record.FailedAttempts >= MaxAttempts)
            {
                return Denied();
            }

            var record = current.Record;
            if (!Passcodes.WellFormed(code) || !Tokens.FixedTimeEquals(record.CodeHash, this.Digest(caller, record.NewEmail, code.Trim())))
            {
                if (await data.SaveAsync(Change.Update(record with { FailedAttempts = record.FailedAttempts + 1 }, current.Version), cancellationToken))
                {
                    return Denied();
                }

                continue;
            }

            var lineage = record.Bindings.Where(binding => binding != Credentials.UserState(caller.Id, record.AuthStamp ?? string.Empty)).ToArray();
            if (record.Bindings.Count == 0 || !await credentials.AreCurrentAsync(caller.InstanceId, record.Bindings, cancellationToken)
                || await credentials.RequireAsync(caller.InstanceId, lineage, cancellationToken) is not { } requirements)
            {
                return Denied();
            }

            var changed = await users.ChangeEmailAsync(caller.InstanceId, caller.Id, caller.AuthEpoch, caller.AuthStamp, record.NewEmail,
                [Change.Delete(record, current.Version), .. requirements],
                cancellationToken);
            if (changed == true)
            {
                return new Done();
            }

            return changed == false ? EmailTaken() : Denied();
        }

        return Denied();
    }

    public async Task<bool> CancelAsync(string userId, string token, CancellationToken cancellationToken)
    {
        const string Instance = Oidc.OidcIssuer.InstanceId;
        if (!PlatformDirectory.IsId(userId) || token is not { Length: 43 }
            || await this.ReadAsync(Instance, userId, cancellationToken) is not { } current
            || !Tokens.FixedTimeEquals(current.Record.CancelHash, Tokens.Sha256Hex(token)))
        {
            return false;
        }

        if (!await sessions.RevokeAllAsync(Instance, userId, cancellationToken))
        {
            return false;
        }

        await data.SaveAsync(Change.Delete(current.Record, current.Version), cancellationToken);
        return true;
    }

    private async Task<(EmailChangeRecord Record, string Version)?> ReadAsync(string instanceId, string userId, CancellationToken cancellationToken)
    {
        var rowData = await this.ReadStoredAsync(userId, cancellationToken);
        return rowData?.Value is { } record
            && record.InstanceId == instanceId && record.UserId == userId && record.ExpiresAt > clock.GetUtcNow()
                ? (record, rowData.Version) : null;
    }

    private async Task<Versioned<EmailChangeRecord>?> ReadStoredAsync(string userId, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<EmailChangeRecord>(
            "SELECT * FROM email_changes WHERE user_id = @userId", new { userId }), cancellationToken));

    private string Digest(UserRecord caller, string newEmail, string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.digestKey, Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{caller.InstanceId.Length}:{caller.InstanceId}{caller.Id.Length}:{caller.Id}{newEmail.Length}:{newEmail}{code}"))));

    private static Forbidden Denied() => new("No matching pending email change; request a new one.", "email_change_denied");

    private static Conflict EmailTaken() => new("Another account already uses that email address.", "email_taken");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EmailChangeRequest))]
[JsonSerializable(typeof(ConfirmEmailChangeRequest))]
[JsonSerializable(typeof(EmailChangeBody))]
internal sealed partial class EmailChangeJson : JsonSerializerContext;
