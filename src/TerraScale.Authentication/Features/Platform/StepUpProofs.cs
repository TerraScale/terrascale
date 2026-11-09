using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Starts fresh verification; the return URI is a registered dashboard origin's fixed callback.</summary>
public sealed record StepUpRequest(
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("return_uri")] string ReturnUri,
    [property: JsonPropertyName("organization_id")] string? OrganizationId = null);

/// <summary>The pending secret stays on the calling backend; the browser receives only StepUpUrl.</summary>
public sealed record StepUpBody(
    [property: JsonPropertyName("proof")] string Proof,
    [property: JsonPropertyName("step_up_url")] string StepUpUrl,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    /// <inheritdoc />
    public override string ToString() => nameof(StepUpBody);
}

/// <summary>Consumes a dashboard proof before side effects; deletion binds its exact receipt.</summary>
public sealed record ConsumeStepUpRequest(
    [property: JsonPropertyName("proof")] string Proof,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("operation_receipt")] string? OperationReceipt = null)
{
    /// <inheritdoc />
    public override string ToString() => nameof(ConsumeStepUpRequest);
}

internal sealed record StepUpRecord(string InstanceId, string Id, string UserId, long AuthEpoch, string? AuthStamp,
    string TokenHash, string Purpose, string Audience, string ReturnUri, DateTimeOffset ExpiresAt,
    bool PrimaryVerified = false, bool Approved = false, bool Consumed = false, bool MailRequested = false,
    IReadOnlyList<CredentialBinding>? Bindings = null, string? OrganizationId = null);
internal sealed record StepUpDeletionGrant(string InstanceId, string UserId, long AuthEpoch, string? AuthStamp, string ReceiptHash,
    DateTimeOffset ExpiresAt, string AuthorizationHash, IReadOnlyList<CredentialBinding> Bindings);

/// <summary>The deletion authorization acknowledgement; release only after the proof's final deadline check.</summary>
public sealed record StepUpGrantBody([property: JsonPropertyName("operation_grant")] string OperationGrant)
{
    /// <inheritdoc />
    public override string ToString() => nameof(StepUpGrantBody);
}

/// <summary>A verified caller and optional exact-deletion capability. Never log this result.</summary>
public sealed record StepUpConsumption(UserRecord Caller, string? OperationGrant)
{
    /// <inheritdoc />
    public override string ToString() => nameof(StepUpConsumption);
}

internal sealed record PreparedStepUp(UserRecord Caller, StepUpRecord Record, IReadOnlyList<StoreOperation> Operations, string? Authorization)
{
    public override string ToString() => nameof(PreparedStepUp);
}

/// <summary>Durable, five-minute purpose proofs. TerraBase transactions serialize approval and consumption.</summary>
public sealed class StepUpProofs(AuthStore store, AuthOptions options, TimeProvider clock, AttemptLimiter limiter)
{
    /// <summary>Maximum pending proof issuances per account in one fixed window on the GRU authority.</summary>
    public const int MaxRequests = 10;

    /// <summary>The account issuance window; not renewed by a replacement request.</summary>
    public static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(15);

    /// <summary>The bearer header for auth-owned sensitive operations.</summary>
    public const string Header = "X-TerraScale-Step-Up-Proof";

    /// <summary>Separate acknowledgement capability required with the exact deletion receipt.</summary>
    public const string DeletionAuthorizationHeader = "X-TerraScale-Deletion-Authorization";

    /// <summary>Consumes the auth API's exact purpose proof from one header value.</summary>
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

    /// <summary>Creates a pending proof; an existing sign-in never approves it.</summary>
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

        var id = AuthStore.NewId();
        var secret = id + "." + Tokens.RandomToken(43);
        var record = new StepUpRecord(caller.InstanceId, id, caller.Id, caller.AuthEpoch, caller.AuthStamp, Tokens.Sha256Hex(secret),
            request.Purpose, request.Audience, callback.AbsoluteUri, clock.GetUtcNow().AddMinutes(5), OrganizationId: request.OrganizationId);
        if (await store.TransactAsync([Current(caller), AuthStore.Create(AuthStore.StepUpProofTable, caller.InstanceId, id, Serialize(record))], cancellationToken).ConfigureAwait(false) is not null)
        {
            throw Required();
        }

        return new StepUpBody(secret, new Uri(options.PublicOrigin, "/ui/terrascale/step-up/" + id).AbsoluteUri, record.ExpiresAt);
    }

    internal async Task<(StepUpRecord Record, string Version)?> ReadAsync(UserRecord caller, string id, CancellationToken cancellationToken)
    {
        var rowData = await store.GetAsync(AuthStore.StepUpProofTable, caller.InstanceId, id, cancellationToken).ConfigureAwait(false);
        return rowData?.Body.Deserialize(StepUpJson.Default.StepUpRecord) is { } record
            && record.InstanceId == caller.InstanceId && record.UserId == caller.Id && record.Id == id
            && record.AuthEpoch == caller.AuthEpoch && record.AuthStamp == caller.AuthStamp && !record.Consumed && record.ExpiresAt > clock.GetUtcNow()
                ? (record, rowData.Version) : null;
    }

    internal async Task<bool> AdvanceAsync(UserRecord caller, StepUpRecord record, string version, CancellationToken cancellationToken)
    {
        if (record.ExpiresAt <= clock.GetUtcNow()
            || !await store.AreCredentialsCurrentAsync(caller.InstanceId, record.Bindings ?? [], cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await store.TransactAsync([.. Requirements(Merge(caller, record.Bindings ?? [])), AuthStore.Replace(AuthStore.StepUpProofTable, caller.InstanceId, record.Id, Serialize(record), version)], cancellationToken).ConfigureAwait(false) is null
            && record.ExpiresAt > clock.GetUtcNow();
    }

    /// <summary>Consumes exactly one approved proof with the current account state, optionally binding one deletion operation.</summary>
    public async Task<StepUpConsumption> ConsumeAsync(UserRecord caller, string? proof, string purpose, string audience, string? receiptHash, CancellationToken cancellationToken, string? organizationId = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await this.PrepareAsync(caller, proof, purpose, audience, receiptHash, cancellationToken, organizationId).ConfigureAwait(false) is not { } prepared)
        {
            throw Required();
        }

        if (prepared.Record.ExpiresAt <= clock.GetUtcNow() || await store.TransactAsync(prepared.Operations, cancellationToken).ConfigureAwait(false) is not null
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
            || !await store.AreCredentialsCurrentAsync(caller.InstanceId, current.Record.Bindings ?? [], cancellationToken).ConfigureAwait(false)
            || current.Record.ExpiresAt <= clock.GetUtcNow())
        {
            return null;
        }

        var verified = Merge(caller, current.Record.Bindings ?? []);
        List<StoreOperation> operations = [.. Requirements(verified), AuthStore.Replace(AuthStore.StepUpProofTable, caller.InstanceId, current.Record.Id,
            Serialize(current.Record with { Consumed = true }), current.Version)];
        var authorization = receiptHash is null ? null : Tokens.RandomToken(43);
        if (receiptHash is not null)
        {
            var grant = new StepUpDeletionGrant(caller.InstanceId, caller.Id, caller.AuthEpoch, caller.AuthStamp, receiptHash,
                clock.GetUtcNow().AddDays(15), Tokens.Sha256Hex(authorization!), current.Record.Bindings ?? []);
            operations.Add(AuthStore.Create(AuthStore.StepUpDeletionGrantTable, caller.InstanceId, receiptHash, JsonSerializer.SerializeToElement(grant, StepUpJson.Default.StepUpDeletionGrant)));
        }

        return new PreparedStepUp(verified, current.Record, operations, authorization);
    }

    /// <summary>Authorizes only this account's exact deletion continuation; no proof can be re-used.</summary>
    public async Task<StepUpConsumption> DeleteAsync(UserRecord caller, HttpRequest request, string? receiptHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (receiptHash is not null && !request.Headers.ContainsKey(Header))
        {
            var stored = await store.GetAsync(AuthStore.StepUpDeletionGrantTable, caller.InstanceId, receiptHash, cancellationToken).ConfigureAwait(false);
            if (stored?.Body.Deserialize(StepUpJson.Default.StepUpDeletionGrant) is { } grant
                && grant.InstanceId == caller.InstanceId && grant.UserId == caller.Id && grant.AuthEpoch == caller.AuthEpoch
                && grant.AuthStamp == caller.AuthStamp && grant.ReceiptHash == receiptHash && grant.ExpiresAt > clock.GetUtcNow()
                && grant.Bindings.Count > 0
                && request.Headers[DeletionAuthorizationHeader] is [string { Length: 43 } authorization]
                && Tokens.FixedTimeEquals(grant.AuthorizationHash, Tokens.Sha256Hex(authorization))
                && await store.AreCredentialsCurrentAsync(caller.InstanceId, grant.Bindings, cancellationToken).ConfigureAwait(false))
            {
                return new StepUpConsumption(Merge(caller, grant.Bindings), null);
            }
        }

        return await this.ConsumeAsync(caller, request.Headers[Header].Count == 1 ? request.Headers[Header][0] : null, "delete_account", "ts-auth", receiptHash, cancellationToken).ConfigureAwait(false);
    }

    private static UserRecord Merge(UserRecord caller, IReadOnlyList<CredentialBinding> fresh)
    {
        var bindings = caller.AuthenticationBindings.Concat(fresh).Distinct().ToArray();
        if (bindings.Length > AuthStore.MaxCredentialBindings)
        {
            throw Required();
        }

        return caller with { AuthenticationBindings = bindings, StepUpBindings = fresh };
    }

    internal static IReadOnlyList<StoreOperation> Requirements(UserRecord caller) =>
        [Current(caller), .. AuthStore.CredentialRequirements(caller.InstanceId, caller.AuthenticationBindings
            .Where(binding => binding != AuthStore.UserStateBinding(caller.Id, caller.AuthStamp!)).ToArray())];

    /// <summary>Deletes expired proof, deletion grant, isolated code and email-change rows; immutable winner claims outlive descendants.</summary>
    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var now = JsonSerializer.Serialize(clock.GetUtcNow(), IdentityJson.Default.DateTimeOffset);
        using var filter = JsonDocument.Parse($$"""{"lte":["/expiresAt",{{now}}]}""");
        var total = 0;
        foreach (var table in new[] { AuthStore.StepUpProofTable, AuthStore.StepUpDeletionGrantTable, AuthStore.StepUpPasscodeTable, AuthStore.EmailChangeTable })
        {
            while (true)
            {
                var batch = await store.QueryAsync(table, filter.RootElement, batchSize, cancellationToken).ConfigureAwait(false);
                var deleted = 0;
                foreach (var rowData in batch)
                {
                    var instance = rowData.Body.GetProperty("instanceId").GetString()!;
                    var key = rowData.Body.GetProperty(table switch
                    {
                        AuthStore.StepUpDeletionGrantTable => "receiptHash",
                        AuthStore.StepUpPasscodeTable => "stepUpRequestId",
                        AuthStore.EmailChangeTable => "userId",
                        _ => "id",
                    }).GetString()!;
                    if (await store.CompareAndDeleteAsync(table, instance, key, rowData.Version, cancellationToken).ConfigureAwait(false))
                    {
                        deleted++;
                    }
                }

                total += deleted;
                if (batch.Count < batchSize || deleted == 0)
                {
                    break;
                }
            }
        }

        return total;
    }

    internal static ApiException Required() => new(403, "step_up_required", "A fresh, approved, purpose-bound single-use proof is required.");
    private static bool Pair(string purpose, string audience) => audience switch
    {
        "ts-dashboard" => purpose is "delete_account" or "create_pat",
        "ts-auth" => purpose is "delete_account" or "delete_organization" or "start_ownership_transfer" or "change_plan" or "delete_passkey" or "create_api_key" or "rotate_api_key" or "create_oauth_client" or "update_oauth_client" or "rotate_oauth_client" or "change_email" or "add_passkey" or "enroll_totp" or "create_webhook_endpoint" or "rotate_webhook_secret",
        _ => false,
    };
    private static bool Bound(string purpose) => purpose is "delete_organization" or "start_ownership_transfer" or "change_plan";
    private static JsonElement Serialize(StepUpRecord value) => JsonSerializer.SerializeToElement(value, StepUpJson.Default.StepUpRecord);
    private static StoreOperation Current(UserRecord user) => AuthStore.Require(AuthStore.UserTable, user.InstanceId, user.Id,
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonObject { ["eq"] = new JsonArray("/authEpoch", user.AuthEpoch) },
                new JsonObject { ["eq"] = new JsonArray("/authStamp", user.AuthStamp) },
                new JsonObject { ["ne"] = new JsonArray("/disabled", true) }),
        }));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(StepUpRecord))]
[JsonSerializable(typeof(StepUpDeletionGrant))]
[JsonSerializable(typeof(StepUpRequest))]
[JsonSerializable(typeof(StepUpBody))]
[JsonSerializable(typeof(ConsumeStepUpRequest))]
[JsonSerializable(typeof(StepUpGrantBody))]
internal sealed partial class StepUpJson : JsonSerializerContext;
