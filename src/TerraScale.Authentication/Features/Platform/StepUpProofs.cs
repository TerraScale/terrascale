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
    [property: JsonPropertyName("return_uri")] string ReturnUri);

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
    IReadOnlyList<CredentialBinding>? Bindings = null);
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
    public async Task<UserRecord> ConsumeHeaderAsync(UserRecord caller, HttpRequest request, string purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await this.ConsumeAsync(caller, request.Headers[Header].Count == 1 ? request.Headers[Header][0] : null, purpose, "ts-auth", null, cancellationToken).ConfigureAwait(false)).Caller;
    }

    /// <summary>Creates a pending proof; an existing sign-in never approves it.</summary>
    public async Task<StepUpBody> RequestAsync(UserRecord caller, StepUpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (!Pair(request.Purpose, request.Audience)
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
            request.Purpose, request.Audience, callback.AbsoluteUri, clock.GetUtcNow().AddMinutes(5));
        if (await store.TransactAsync([Current(caller), AuthStore.Create(AuthStore.StepUpProofCollection, caller.InstanceId, id, Serialize(record))], cancellationToken).ConfigureAwait(false) is not null)
        {
            throw Required();
        }

        return new StepUpBody(secret, new Uri(options.PublicOrigin, "/ui/terrascale/step-up/" + id).AbsoluteUri, record.ExpiresAt);
    }

    internal async Task<(StepUpRecord Record, string Version)?> ReadAsync(UserRecord caller, string id, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(AuthStore.StepUpProofCollection, caller.InstanceId, id, cancellationToken).ConfigureAwait(false);
        return document?.Document.Deserialize(StepUpJson.Default.StepUpRecord) is { } record
            && record.InstanceId == caller.InstanceId && record.UserId == caller.Id && record.Id == id
            && record.AuthEpoch == caller.AuthEpoch && record.AuthStamp == caller.AuthStamp && !record.Consumed && record.ExpiresAt > clock.GetUtcNow()
                ? (record, document.Version) : null;
    }

    internal async Task<bool> AdvanceAsync(UserRecord caller, StepUpRecord record, string version, CancellationToken cancellationToken)
    {
        if (record.ExpiresAt <= clock.GetUtcNow()
            || !await store.AreCredentialsCurrentAsync(caller.InstanceId, record.Bindings ?? [], cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await store.TransactAsync([.. Requirements(Merge(caller, record.Bindings ?? [])), AuthStore.Replace(AuthStore.StepUpProofCollection, caller.InstanceId, record.Id, Serialize(record), version)], cancellationToken).ConfigureAwait(false) is null
            && record.ExpiresAt > clock.GetUtcNow();
    }

    /// <summary>Consumes exactly one approved proof with the current account state, optionally binding one deletion operation.</summary>
    public async Task<StepUpConsumption> ConsumeAsync(UserRecord caller, string? proof, string purpose, string audience, string? receiptHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!Pair(purpose, audience) || receiptHash is not null && purpose != "delete_account"
            || proof is not { Length: 70 } || proof[26] != '.' || !PlatformDirectory.IsId(proof[..26])
            || await this.ReadAsync(caller, proof[..26], cancellationToken).ConfigureAwait(false) is not { Record: { Approved: true, PrimaryVerified: true, Bindings.Count: > 0 } } current
            || current.Record.Purpose != purpose || current.Record.Audience != audience
            || !Tokens.FixedTimeEquals(current.Record.TokenHash, Tokens.Sha256Hex(proof))
            || !await store.AreCredentialsCurrentAsync(caller.InstanceId, current.Record.Bindings ?? [], cancellationToken).ConfigureAwait(false))
        {
            throw Required();
        }

        var verified = Merge(caller, current.Record.Bindings ?? []);
        List<StoreOperation> operations = [.. Requirements(verified), AuthStore.Replace(AuthStore.StepUpProofCollection, caller.InstanceId, current.Record.Id,
            Serialize(current.Record with { Consumed = true }), current.Version)];
        var authorization = receiptHash is null ? null : Tokens.RandomToken(43);
        if (receiptHash is not null)
        {
            var grant = new StepUpDeletionGrant(caller.InstanceId, caller.Id, caller.AuthEpoch, caller.AuthStamp, receiptHash,
                clock.GetUtcNow().AddDays(15), Tokens.Sha256Hex(authorization!), current.Record.Bindings ?? []);
            operations.Add(AuthStore.Create(AuthStore.StepUpDeletionGrantCollection, caller.InstanceId, receiptHash, JsonSerializer.SerializeToElement(grant, StepUpJson.Default.StepUpDeletionGrant)));
        }

        if (current.Record.ExpiresAt <= clock.GetUtcNow() || await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is not null
            || current.Record.ExpiresAt <= clock.GetUtcNow())
        {
            throw Required();
        }

        // No acknowledgement secret is persisted or recoverable. A late commit or lost response cannot authorize DELETE.
        return new StepUpConsumption(verified, authorization);
    }

    /// <summary>Authorizes only this account's exact deletion continuation; no proof can be re-used.</summary>
    public async Task<StepUpConsumption> DeleteAsync(UserRecord caller, HttpRequest request, string? receiptHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (receiptHash is not null && !request.Headers.ContainsKey(Header))
        {
            var stored = await store.GetAsync(AuthStore.StepUpDeletionGrantCollection, caller.InstanceId, receiptHash, cancellationToken).ConfigureAwait(false);
            if (stored?.Document.Deserialize(StepUpJson.Default.StepUpDeletionGrant) is { } grant
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

    /// <summary>Deletes expired proof, deletion grant and isolated code rows; immutable winner claims outlive descendants.</summary>
    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var now = JsonSerializer.Serialize(clock.GetUtcNow(), IdentityJson.Default.DateTimeOffset);
        using var filter = JsonDocument.Parse($$"""{"lte":["/expiresAt",{{now}}]}""");
        var total = 0;
        foreach (var collection in new[] { AuthStore.StepUpProofCollection, AuthStore.StepUpDeletionGrantCollection, AuthStore.StepUpPasscodeCollection })
        {
            while (true)
            {
                var batch = await store.QueryAsync(collection, filter.RootElement, batchSize, cancellationToken).ConfigureAwait(false);
                var deleted = 0;
                foreach (var document in batch)
                {
                    var instance = document.Document.GetProperty("instanceId").GetString()!;
                    var key = document.Document.GetProperty(collection switch
                    {
                        AuthStore.StepUpDeletionGrantCollection => "receiptHash",
                        AuthStore.StepUpPasscodeCollection => "stepUpRequestId",
                        _ => "id",
                    }).GetString()!;
                    if (await store.CompareAndDeleteAsync(collection, instance, key, document.Version, cancellationToken).ConfigureAwait(false))
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
        "ts-auth" => purpose is "delete_account" or "create_api_key" or "rotate_api_key" or "create_oauth_client",
        _ => false,
    };
    private static JsonElement Serialize(StepUpRecord value) => JsonSerializer.SerializeToElement(value, StepUpJson.Default.StepUpRecord);
    private static StoreOperation Current(UserRecord user) => AuthStore.Require(AuthStore.UserCollection, user.InstanceId, user.Id,
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
