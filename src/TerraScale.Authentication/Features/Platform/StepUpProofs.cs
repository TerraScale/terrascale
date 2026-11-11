using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Platform;

public sealed record StepUpRequest(
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("return_uri")] string ReturnUri,
    [property: JsonPropertyName("organization_id")] string? OrganizationId = null);

public sealed record StepUpBody(
    [property: JsonPropertyName("proof")] string Proof,
    [property: JsonPropertyName("step_up_url")] string StepUpUrl,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(StepUpBody);
}

public sealed record ConsumeStepUpRequest(
    [property: JsonPropertyName("proof")] string Proof,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("operation_receipt")] string? OperationReceipt = null,
    [property: JsonPropertyName("organization_id")] string? OrganizationId = null)
{
    public override string ToString() => nameof(ConsumeStepUpRequest);
}

internal sealed record StepUpRecord(string InstanceId, string Id, string UserId, long AuthEpoch, string? AuthStamp,
    string TokenHash, string Purpose, string Audience, string ReturnUri, DateTimeOffset ExpiresAt,
    bool PrimaryVerified = false, bool Approved = false, bool Consumed = false, bool MailRequested = false,
    IReadOnlyList<CredentialBinding>? Bindings = null, string? OrganizationId = null);
internal sealed record StepUpDeletionGrant(string InstanceId, string UserId, long AuthEpoch, string? AuthStamp, string ReceiptHash,
    DateTimeOffset ExpiresAt, string AuthorizationHash, IReadOnlyList<CredentialBinding> Bindings);

public sealed record StepUpGrantBody([property: JsonPropertyName("operation_grant")] string OperationGrant)
{
    public override string ToString() => nameof(StepUpGrantBody);
}

public sealed record StepUpConsumption(UserRecord Caller, string? OperationGrant)
{
    public override string ToString() => nameof(StepUpConsumption);
}

internal sealed record PreparedStepUp(UserRecord Caller, StepUpRecord Record, IReadOnlyList<Change> Operations, string? Authorization)
{
    public override string ToString() => nameof(PreparedStepUp);
}

public sealed class StepUpProofs(AuthData data, Credentials credentials, AuthOptions options, TimeProvider clock, AttemptLimiter limiter)
{
    public const int MaxRequests = 10;

    public static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(15);

    public const string Header = "X-TerraScale-Step-Up-Proof";

    public const string DeletionAuthorizationHeader = "X-TerraScale-Deletion-Authorization";

    public async Task<UserRecord> ConsumeHeaderAsync(UserRecord caller, HttpRequest request, string purpose, string? organizationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await this.ConsumeAsync(caller, request.Headers[Header].Count == 1 ? request.Headers[Header][0] : null, purpose, "ts-auth", null, cancellationToken, organizationId).ConfigureAwait(false)).Caller;
    }

    internal async Task<PreparedStepUp?> PrepareHeaderAsync(UserRecord caller, HttpRequest request, string purpose, string? organizationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await this.PrepareAsync(caller, request.Headers[Header].Count == 1 ? request.Headers[Header][0] : null, purpose, "ts-auth", null, cancellationToken, organizationId).ConfigureAwait(false);
    }

    public async Task<StepUpBody> RequestAsync(UserRecord caller, StepUpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (!Pair(request.Purpose, request.Audience)
            || Bound(request.Purpose) != (request.OrganizationId is not null)
            || request.OrganizationId is not null && !PlatformDirectory.IsId(request.OrganizationId)
            || !Uri.TryCreate(request.ReturnUri, UriKind.Absolute, out var callback)
            || callback.AbsolutePath != "/auth/step-up/callback" || callback.Query.Length != 0 || callback.Fragment.Length != 0 || callback.UserInfo.Length != 0
            || options.DashboardClient?.RedirectUris.Any(uri => uri.GetLeftPart(UriPartial.Authority) == callback.GetLeftPart(UriPartial.Authority)) != true)
        {
            throw new ApiException(400, "invalid_request", "Use a supported purpose/audience and the registered dashboard origin's /auth/step-up/callback.");
        }

        if (!await limiter.TryAcquireAsync(caller.InstanceId, "step-up-issuance", caller.Id, MaxRequests, RequestWindow, cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(429, "rate_limited", "Too many verification requests; retry after fifteen minutes.");
        }

        var id = AuthIds.New();
        var secret = id + "." + Tokens.RandomToken(43);
        var record = new StepUpRecord(caller.InstanceId, id, caller.Id, caller.AuthEpoch, caller.AuthStamp, Tokens.Sha256Hex(secret),
            request.Purpose, request.Audience, callback.AbsoluteUri, clock.GetUtcNow().AddMinutes(5), OrganizationId: request.OrganizationId);
        if (!await data.SaveAsync([Change.Assert(caller), Change.Insert(record)], cancellationToken).ConfigureAwait(false))
        {
            throw Required();
        }

        return new StepUpBody(secret, new Uri(options.PublicOrigin, "/ui/terrascale/step-up/" + id).AbsoluteUri, record.ExpiresAt);
    }

    internal async Task<(StepUpRecord Record, string Version)?> ReadAsync(UserRecord caller, string id, CancellationToken cancellationToken)
    {
        var rowData = await data.ReadAsync((db, token) => db.StepUpProofs.Where(proof => proof.Id == id).WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        return rowData?.Value is { } record
            && record.InstanceId == caller.InstanceId && record.UserId == caller.Id && record.Id == id
            && record.AuthEpoch == caller.AuthEpoch && record.AuthStamp == caller.AuthStamp && !record.Consumed && record.ExpiresAt > clock.GetUtcNow()
                ? (record, rowData.Version) : null;
    }

    internal async Task<bool> AdvanceAsync(UserRecord caller, StepUpRecord record, string version, CancellationToken cancellationToken)
    {
        if (record.ExpiresAt <= clock.GetUtcNow()
            || await this.RequirementsAsync(Merge(caller, record.Bindings ?? []), cancellationToken).ConfigureAwait(false) is not { } requirements)
        {
            return false;
        }

        return await data.SaveAsync([.. requirements, Change.Update(record, version)], cancellationToken).ConfigureAwait(false)
            && record.ExpiresAt > clock.GetUtcNow();
    }

    public async Task<StepUpConsumption> ConsumeAsync(UserRecord caller, string? proof, string purpose, string audience, string? receiptHash, CancellationToken cancellationToken, string? organizationId = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await this.PrepareAsync(caller, proof, purpose, audience, receiptHash, cancellationToken, organizationId).ConfigureAwait(false) is not { } prepared)
        {
            throw Required();
        }

        if (prepared.Record.ExpiresAt <= clock.GetUtcNow() || !await data.SaveAsync(prepared.Operations, cancellationToken).ConfigureAwait(false)
            || prepared.Record.ExpiresAt <= clock.GetUtcNow())
        {
            throw Required();
        }

        return new StepUpConsumption(prepared.Caller, prepared.Authorization);
    }

    internal async Task<PreparedStepUp?> PrepareAsync(UserRecord caller, string? proof, string purpose, string audience, string? receiptHash, CancellationToken cancellationToken, string? organizationId = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!Pair(purpose, audience) || receiptHash is not null && purpose != "delete_account"
            || proof is not { Length: 70 } || proof[26] != '.' || !PlatformDirectory.IsId(proof[..26])
            || await this.ReadAsync(caller, proof[..26], cancellationToken).ConfigureAwait(false) is not { Record: { Approved: true, PrimaryVerified: true, Bindings.Count: > 0 } } current
            || current.Record.Purpose != purpose || current.Record.Audience != audience
            || current.Record.OrganizationId != organizationId
            || !Tokens.FixedTimeEquals(current.Record.TokenHash, Tokens.Sha256Hex(proof))
            || current.Record.ExpiresAt <= clock.GetUtcNow())
        {
            return null;
        }

        var verified = Merge(caller, current.Record.Bindings ?? []);
        if (await this.RequirementsAsync(verified, cancellationToken).ConfigureAwait(false) is not { } requirements)
        {
            return null;
        }

        List<Change> operations = [.. requirements, Change.Update(current.Record with { Consumed = true }, current.Version)];
        var authorization = receiptHash is null ? null : Tokens.RandomToken(43);
        if (receiptHash is not null)
        {
            var grant = new StepUpDeletionGrant(caller.InstanceId, caller.Id, caller.AuthEpoch, caller.AuthStamp, receiptHash,
                clock.GetUtcNow().AddDays(15), Tokens.Sha256Hex(authorization!), current.Record.Bindings ?? []);
            operations.Add(Change.Insert(grant));
        }

        return new PreparedStepUp(verified, current.Record, operations, authorization);
    }

    public async Task<StepUpConsumption> DeleteAsync(UserRecord caller, HttpRequest request, string? receiptHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (receiptHash is not null && !request.Headers.ContainsKey(Header))
        {
            if (await data.ReadAsync((db, token) => db.StepUpDeletionGrants.SingleOrDefaultAsync(row => row.InstanceId == caller.InstanceId && row.ReceiptHash == receiptHash, token),
                cancellationToken).ConfigureAwait(false) is { } grant
                && grant.InstanceId == caller.InstanceId && grant.UserId == caller.Id && grant.AuthEpoch == caller.AuthEpoch
                && grant.AuthStamp == caller.AuthStamp && grant.ReceiptHash == receiptHash && grant.ExpiresAt > clock.GetUtcNow()
                && grant.Bindings.Count > 0
                && request.Headers[DeletionAuthorizationHeader] is [string { Length: 43 } authorization]
                && Tokens.FixedTimeEquals(grant.AuthorizationHash, Tokens.Sha256Hex(authorization))
                && await credentials.AreCurrentAsync(caller.InstanceId, grant.Bindings, cancellationToken).ConfigureAwait(false))
            {
                return new StepUpConsumption(Merge(caller, grant.Bindings), null);
            }
        }

        return await this.ConsumeAsync(caller, request.Headers[Header].Count == 1 ? request.Headers[Header][0] : null, "delete_account", "ts-auth", receiptHash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserRecord> DeleteOrganizationAsync(UserRecord caller, HttpRequest request, string organizationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers[Header] is not [string { Length: 70 } proof] || proof[26] != '.' || !PlatformDirectory.IsId(proof[..26]))
        {
            throw Required();
        }

        var id = proof[..26];
        var current = await data.ReadAsync((db, token) => db.StepUpProofs.Where(row => row.Id == id).WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        if (current?.Value is not { Consumed: true, Approved: true, PrimaryVerified: true, Purpose: "delete_organization", Audience: "ts-auth", Bindings: { Count: > 0 } bindings } record
            || record.InstanceId != caller.InstanceId || record.UserId != caller.Id || record.Id != id
            || record.AuthEpoch != caller.AuthEpoch || record.AuthStamp != caller.AuthStamp || record.OrganizationId != organizationId
            || !Tokens.FixedTimeEquals(record.TokenHash, Tokens.Sha256Hex(proof))
            || record.ExpiresAt <= clock.GetUtcNow())
        {
            throw Required();
        }

        var verified = Merge(caller, bindings);
        if (await this.RequirementsAsync(verified, cancellationToken).ConfigureAwait(false) is not { } requirements
            || !await data.SaveAsync([.. requirements, Change.Delete(current)], cancellationToken).ConfigureAwait(false)
            || record.ExpiresAt <= clock.GetUtcNow())
        {
            throw Required();
        }

        return verified;
    }

    private static UserRecord Merge(UserRecord caller, IReadOnlyList<CredentialBinding> fresh)
    {
        var bindings = caller.AuthenticationBindings.Concat(fresh).Distinct().ToArray();
        if (bindings.Length > Credentials.MaxBindings)
        {
            throw Required();
        }

        return caller with { AuthenticationBindings = bindings, StepUpBindings = fresh };
    }

    internal async Task<IReadOnlyList<Change>?> RequirementsAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await credentials.RequireAsync(caller.InstanceId, [.. caller.AuthenticationBindings.Where(binding => binding != Credentials.UserState(caller.Id, caller.AuthStamp!))],
            cancellationToken).ConfigureAwait(false) is { } lineage ? [Change.Assert(caller), .. lineage] : null;

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var now = clock.GetUtcNow();
        return await data.ReadAsync(async (db, token) =>
            await db.StepUpProofs.Where(row => row.ExpiresAt <= now).ExecuteDeleteAsync(token).ConfigureAwait(false)
            + await db.StepUpDeletionGrants.Where(row => row.ExpiresAt <= now).ExecuteDeleteAsync(token).ConfigureAwait(false)
            + await db.Passcodes.Where(row => row.Purpose == "step-up" && row.ExpiresAt <= now).ExecuteDeleteAsync(token).ConfigureAwait(false)
            + await db.EmailChanges.Where(row => row.ExpiresAt <= now).ExecuteDeleteAsync(token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    internal static ApiException Required() => new(403, "step_up_required", "A fresh, approved, purpose-bound single-use proof is required.");
    private static bool Pair(string purpose, string audience) => audience switch
    {
        "ts-dashboard" => purpose is "delete_account" or "create_pat",
        "ts-auth" => purpose is "delete_account" or "delete_organization" or "start_ownership_transfer" or "change_plan" or "delete_passkey" or "rotate_api_key" or "create_oauth_client" or "update_oauth_client" or "rotate_oauth_client" or "change_email" or "add_passkey" or "create_webhook_endpoint" or "rotate_webhook_secret",
        _ => false,
    };
    private static bool Bound(string purpose) => purpose is "delete_organization" or "start_ownership_transfer" or "change_plan";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(StepUpRequest))]
[JsonSerializable(typeof(StepUpBody))]
[JsonSerializable(typeof(ConsumeStepUpRequest))]
[JsonSerializable(typeof(StepUpGrantBody))]
internal sealed partial class StepUpJson : JsonSerializerContext;
