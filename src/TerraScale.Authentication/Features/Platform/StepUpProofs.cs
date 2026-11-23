using System.Text.Json.Serialization;
using Dapper;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
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

public sealed record StepUpRecord(string InstanceId, string Id, string UserId, long AuthEpoch, string? AuthStamp,
    string TokenHash, string Purpose, string Audience, string ReturnUri, DateTimeOffset ExpiresAt,
    bool PrimaryVerified = false, bool Approved = false, bool Consumed = false, bool MailRequested = false,
    IReadOnlyList<CredentialBinding>? Bindings = null, string? OrganizationId = null)
{
    public string? Version { get; init; }
}

public sealed record StepUpDeletionGrant(string InstanceId, string UserId, long AuthEpoch, string? AuthStamp, string ReceiptHash,
    DateTimeOffset ExpiresAt, string AuthorizationHash, IReadOnlyList<CredentialBinding> Bindings)
{
    public string? Version { get; init; }
}

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

public readonly union StepUpBodyResult(StepUpBody, Validation, QuotaExceeded, Forbidden);

public readonly union StepUpConsumptionResult(StepUpConsumption, Forbidden);

public readonly union StepUpUserResult(UserRecord, Forbidden);

public sealed class StepUpProofs(AuthData data, Credentials credentials, AuthOptions options, TimeProvider clock, AttemptLimiter limiter)
{
    public const int MaxRequests = 10;

    public static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(15);

    public const string Header = "X-TerraScale-Step-Up-Proof";

    public const string ApproveAgent = "approve_agent";

    public const string DeletionAuthorizationHeader = "X-TerraScale-Deletion-Authorization";

    internal static string? Proof(HttpRequest request) =>
        request.Headers[Header].Count == 1 ? request.Headers[Header][0] : null;

    public async Task<UserRecord> ConsumeHeaderAsync(UserRecord caller, HttpRequest request, string purpose, string? organizationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await this.ConsumeAsync(caller, Proof(request), purpose, "ts-auth", null, cancellationToken, organizationId) switch
        {
            StepUpConsumption consumed => consumed.Caller,
            Forbidden => throw Required(),
        };
    }

    internal async Task<PreparedStepUp?> PrepareHeaderAsync(UserRecord caller, HttpRequest request, string purpose, string? organizationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await this.PrepareAsync(caller, Proof(request), purpose, "ts-auth", null, cancellationToken, organizationId);
    }

    public async Task<StepUpBodyResult> RequestAsync(UserRecord caller, StepUpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (!Pair(request.Purpose, request.Audience) || request.Purpose == ApproveAgent
            || Bound(request.Purpose) != (request.OrganizationId is not null)
            || request.OrganizationId is not null && !ValidBinding(request.Purpose, request.OrganizationId)
            || !Uri.TryCreate(request.ReturnUri, UriKind.Absolute, out var callback)
            || callback.AbsolutePath != "/auth/step-up/callback" || callback.Query.Length != 0 || callback.Fragment.Length != 0 || callback.UserInfo.Length != 0
            || options.DashboardClient?.RedirectUris.Any(uri => uri.GetLeftPart(UriPartial.Authority) == callback.GetLeftPart(UriPartial.Authority)) != true)
        {
            return new Validation("Use a supported purpose/audience and the registered dashboard origin's /auth/step-up/callback.");
        }

        return await this.IssueAsync(caller, request.Purpose, request.Audience, callback, request.OrganizationId, cancellationToken);
    }

    internal async Task<StepUpBody> RequestAgentApprovalAsync(UserRecord caller, string enrollmentId, string returnPath, CancellationToken cancellationToken) =>
        await this.IssueAsync(caller, ApproveAgent, "ts-auth", new Uri(options.PublicOrigin, returnPath), enrollmentId, cancellationToken) switch
        {
            StepUpBody body => body,
            Validation failure => throw new ApiException(StatusCodes.Status400BadRequest, failure.Code, failure.Message),
            QuotaExceeded failure => throw new ApiException(StatusCodes.Status429TooManyRequests, failure.Code, failure.Message),
            Forbidden => throw Required(),
        };

    private async Task<StepUpBodyResult> IssueAsync(UserRecord caller, string purpose, string audience, Uri returnUri, string? binding, CancellationToken cancellationToken)
    {
        if (!await limiter.TryAcquireAsync(caller.InstanceId, "step-up-issuance", caller.Id, MaxRequests, RequestWindow, cancellationToken))
        {
            return new QuotaExceeded("Too many verification requests; retry after fifteen minutes.", "rate_limited");
        }

        var id = AuthIds.New();
        var secret = id + "." + Tokens.RandomToken(43);
        var record = new StepUpRecord(caller.InstanceId, id, caller.Id, caller.AuthEpoch, caller.AuthStamp, Tokens.Sha256Hex(secret),
            purpose, audience, returnUri.AbsoluteUri, clock.GetUtcNow().AddMinutes(5), OrganizationId: binding);
        if (!await data.SaveAsync([Change.Assert(caller), Change.Insert(record)], cancellationToken))
        {
            return StepUpRequired();
        }

        return new StepUpBody(secret, new Uri(options.PublicOrigin, $"/ui/{caller.InstanceId}/step-up/{id}").AbsoluteUri, record.ExpiresAt);
    }

    internal async Task<(StepUpRecord Record, string Version)?> ReadAsync(UserRecord caller, string id, CancellationToken cancellationToken)
    {
        var rowData = await this.ProofAsync(id, cancellationToken);
        return rowData?.Value is { } record
            && record.InstanceId == caller.InstanceId && record.UserId == caller.Id && record.Id == id
            && record.AuthEpoch == caller.AuthEpoch && record.AuthStamp == caller.AuthStamp && !record.Consumed && record.ExpiresAt > clock.GetUtcNow()
                ? (record, rowData.Version) : null;
    }

    internal async Task<bool> AdvanceAsync(UserRecord caller, StepUpRecord record, string version, CancellationToken cancellationToken)
    {
        var verified = Merge(caller, record.Bindings ?? []);
        if (record.ExpiresAt <= clock.GetUtcNow() || verified is null
            || await this.RequirementsAsync(verified, cancellationToken) is not { } requirements)
        {
            return false;
        }

        return await data.SaveAsync([.. requirements, Change.Update(record, version)], cancellationToken)
            && record.ExpiresAt > clock.GetUtcNow();
    }

    public async Task<StepUpConsumptionResult> ConsumeAsync(UserRecord caller, string? proof, string purpose, string audience, string? receiptHash, CancellationToken cancellationToken, string? organizationId = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await this.PrepareAsync(caller, proof, purpose, audience, receiptHash, cancellationToken, organizationId) is not { } prepared)
        {
            return StepUpRequired();
        }

        if (prepared.Record.ExpiresAt <= clock.GetUtcNow() || !await data.SaveAsync(prepared.Operations, cancellationToken)
            || prepared.Record.ExpiresAt <= clock.GetUtcNow())
        {
            return StepUpRequired();
        }

        return new StepUpConsumption(prepared.Caller, prepared.Authorization);
    }

    internal async Task<PreparedStepUp?> PrepareAsync(UserRecord caller, string? proof, string purpose, string audience, string? receiptHash, CancellationToken cancellationToken, string? organizationId = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!Pair(purpose, audience) || receiptHash is not null && purpose != "delete_account"
            || proof is not { Length: 70 } || proof[26] != '.' || !PlatformDirectory.IsId(proof[..26])
            || await this.ReadAsync(caller, proof[..26], cancellationToken) is not { Record: { Approved: true, PrimaryVerified: true, Bindings.Count: > 0 } } current
            || current.Record.Purpose != purpose || current.Record.Audience != audience
            || current.Record.OrganizationId != organizationId
            || !Tokens.FixedTimeEquals(current.Record.TokenHash, Tokens.Sha256Hex(proof))
            || current.Record.ExpiresAt <= clock.GetUtcNow())
        {
            return null;
        }

        var verified = Merge(caller, current.Record.Bindings ?? []);
        if (verified is null || await this.RequirementsAsync(verified, cancellationToken) is not { } requirements)
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

    public async Task<StepUpConsumptionResult> DeleteAsync(UserRecord caller, HttpRequest request, string? receiptHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (receiptHash is not null && !request.Headers.ContainsKey(Header))
        {
            var instance = caller.InstanceId;
            if (await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<StepUpDeletionGrant>(
                "SELECT * FROM step_up_deletion_grants WHERE instance_id = @instance AND receipt_hash = @receiptHash", new { instance, receiptHash }), cancellationToken) is { } grant
                && grant.InstanceId == caller.InstanceId && grant.UserId == caller.Id && grant.AuthEpoch == caller.AuthEpoch
                && grant.AuthStamp == caller.AuthStamp && grant.ReceiptHash == receiptHash && grant.ExpiresAt > clock.GetUtcNow()
                && grant.Bindings.Count > 0
                && request.Headers[DeletionAuthorizationHeader] is [string { Length: 43 } authorization]
                && Tokens.FixedTimeEquals(grant.AuthorizationHash, Tokens.Sha256Hex(authorization))
                && await credentials.AreCurrentAsync(caller.InstanceId, grant.Bindings, cancellationToken))
            {
                var continued = Merge(caller, grant.Bindings);
                if (continued is not null)
                {
                    return new StepUpConsumption(continued, null);
                }
            }
        }

        return await this.ConsumeAsync(caller, Proof(request), "delete_account", "ts-auth", receiptHash, cancellationToken);
    }

    public async Task<StepUpUserResult> DeleteOrganizationAsync(UserRecord caller, HttpRequest request, string organizationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers[Header] is not [string { Length: 70 } proof] || proof[26] != '.' || !PlatformDirectory.IsId(proof[..26]))
        {
            return StepUpRequired();
        }

        var id = proof[..26];
        var current = await this.ProofAsync(id, cancellationToken);
        if (current?.Value is not { Consumed: true, Approved: true, PrimaryVerified: true, Purpose: "delete_organization", Audience: "ts-auth", Bindings: { Count: > 0 } bindings } record
            || record.InstanceId != caller.InstanceId || record.UserId != caller.Id || record.Id != id
            || record.AuthEpoch != caller.AuthEpoch || record.AuthStamp != caller.AuthStamp || record.OrganizationId != organizationId
            || !Tokens.FixedTimeEquals(record.TokenHash, Tokens.Sha256Hex(proof))
            || record.ExpiresAt <= clock.GetUtcNow())
        {
            return StepUpRequired();
        }

        var verified = Merge(caller, bindings);
        if (verified is null || await this.RequirementsAsync(verified, cancellationToken) is not { } requirements
            || !await data.SaveAsync([.. requirements, Change.Delete(current)], cancellationToken)
            || record.ExpiresAt <= clock.GetUtcNow())
        {
            return StepUpRequired();
        }

        return verified;
    }

    private static UserRecord? Merge(UserRecord caller, IReadOnlyList<CredentialBinding> fresh)
    {
        var bindings = caller.AuthenticationBindings.Concat(fresh).Distinct().ToArray();
        if (bindings.Length > Credentials.MaxLineage)
        {
            return null;
        }

        return caller with { AuthenticationBindings = bindings, StepUpBindings = fresh };
    }

    internal async Task<IReadOnlyList<Change>?> RequirementsAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await credentials.RequireAsync(caller.InstanceId, [.. caller.AuthenticationBindings.Where(binding => binding != Credentials.UserState(caller.Id, caller.AuthStamp!))],
            cancellationToken) is { } lineage ? [Change.Assert(caller), .. lineage] : null;

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        return await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
                "DELETE FROM step_up_proofs WHERE id IN (SELECT id FROM step_up_proofs WHERE expires_at <= @now ORDER BY expires_at LIMIT @batchSize)",
                new { now, batchSize }), cancellationToken)
            + await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
                """
                DELETE FROM step_up_deletion_grants WHERE (instance_id, receipt_hash) IN (
                    SELECT instance_id, receipt_hash FROM step_up_deletion_grants WHERE expires_at <= @now ORDER BY expires_at LIMIT @batchSize)
                """, new { now, batchSize }), cancellationToken)
            + await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
                """
                DELETE FROM passcodes WHERE (instance_id, "key") IN (
                    SELECT instance_id, "key" FROM passcodes WHERE purpose = 'step-up' AND expires_at <= @now ORDER BY expires_at LIMIT @batchSize)
                """, new { now, batchSize }), cancellationToken)
            + await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
                "DELETE FROM email_changes WHERE user_id IN (SELECT user_id FROM email_changes WHERE expires_at <= @now ORDER BY expires_at LIMIT @batchSize)",
                new { now, batchSize }), cancellationToken);
    }

    private async Task<Versioned<StepUpRecord>?> ProofAsync(string id, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<StepUpRecord>(
            "SELECT * FROM step_up_proofs WHERE id = @id", new { id }), cancellationToken));

    internal static ApiException Required() => new(403, "step_up_required", "A fresh, approved, purpose-bound single-use proof is required.");

    internal static Forbidden StepUpRequired() => new("A fresh, approved, purpose-bound single-use proof is required.", "step_up_required");
    private static bool Pair(string purpose, string audience) => audience switch
    {
        "ts-dashboard" => purpose is "delete_account" or "create_pat",
        "ts-auth" => purpose is "delete_account" or "delete_organization" or "start_ownership_transfer" or "change_plan" or "delete_passkey" or ApproveAgent or "rotate_api_key" or "create_oauth_client" or "update_oauth_client" or "rotate_oauth_client" or "delete_oauth_client" or "change_email" or "add_passkey" or "create_webhook_endpoint" or "rotate_webhook_secret" or "start_impersonation",
        _ => false,
    };
    private static bool ValidBinding(string purpose, string binding) =>
        purpose == "start_impersonation"
            ? binding.Split(':') is [var project, var target] && PlatformDirectory.IsId(project) && PlatformDirectory.IsId(target)
            : PlatformDirectory.IsId(binding);

    private static bool Bound(string purpose) => purpose is "start_impersonation" or "delete_organization" or "start_ownership_transfer" or "change_plan" or ApproveAgent;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(StepUpRequest))]
[JsonSerializable(typeof(StepUpBody))]
[JsonSerializable(typeof(ConsumeStepUpRequest))]
[JsonSerializable(typeof(StepUpGrantBody))]
internal sealed partial class StepUpJson : JsonSerializerContext;
