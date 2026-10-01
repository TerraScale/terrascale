using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TerraScale.DotNet.Native;

/// <summary>Persisted scope, original identity and sensitive immutable request body; no credential or admission lease is serialized.</summary>
/// <param name="Scope">The immutable provisioned native scope.</param>
/// <param name="ExplicitOrigin">The optional previously admitted direct-origin binding.</param>
/// <param name="Identity">The exact original transaction identity.</param>
/// <param name="RequestBody">The sensitive immutable native request body.</param>
public sealed record NativeRetainedOperation(
    NativeScope Scope,
    string? ExplicitOrigin,
    NativeTransactionIdentity Identity,
    string RequestBody)
{
    /// <inheritdoc />
    public override string ToString() => "Retained native operation";
}

/// <summary>Private local recovery metadata; no API key, OAuth token or discovery grant is serialized.</summary>
/// <remarks>Unix files must be owner-private and stored in a directory with a trusted owner and no group/other write permission. On Windows the caller must provision and verify a trusted private directory hierarchy with private ACLs.</remarks>
public static class NativeOperationStore
{
    private const int MaximumRetainedBytes = NativeCodec.MaximumBodyBytes + 8192;

    /// <summary>Persist before dispatch. Existing operation files are never overwritten.</summary>
    public static async Task SaveAsync(NativePreparedCommit prepared, string path, CancellationToken cancellationToken = default)
    {
        using var body = NativeCodec.Parse(prepared.RequestBody);
        NativeCodec.DecodeCommit(prepared.Scope, body.RootElement, out var identity);
        NativeCodec.RequireIdentity(identity, prepared.Identity);
        var bytes = Encode(prepared);
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Native operation directory must already exist.");
        await NativeFilePersistence.SaveAsync(bytes, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads a private bounded operation file and revalidates its scope and exact native effect.</summary>
    public static async Task<NativeRetainedOperation> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = NativeFilePersistence.OpenPrivateRead(path);
        if (stream.Length > MaximumRetainedBytes) throw new InvalidDataException("Native operation file exceeds its byte limit.");
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaximumRetainedBytes) throw new InvalidDataException("Native operation file exceeds its byte limit.");
            buffer.Write(chunk, 0, count);
        }
        using var document = NativeCodec.ParseLocalOperation(buffer.ToArray());
        var root = document.RootElement;
        Exact(root, ["local_version", "scope", "explicit_origin", "request_body"]);
        var version = root.GetProperty("local_version");
        if (!version.TryGetInt32(out var number) || number != 1) throw new InvalidDataException("Unsupported local operation format.");
        var rawScope = root.GetProperty("scope");
        Exact(rawScope, ["tenant", "database", "shard"]);
        var shard = RetainedString(rawScope.GetProperty("shard"), "shard");
        if (!ulong.TryParse(shard, NumberStyles.None, CultureInfo.InvariantCulture, out var shardNumber) ||
            shardNumber.ToString(CultureInfo.InvariantCulture) != shard)
            throw new InvalidDataException("Retained shard must be canonical u64 decimal.");
        var scope = NativeCodec.ValidateScope(new NativeScope(RetainedString(rawScope.GetProperty("tenant"), "tenant"),
            RetainedString(rawScope.GetProperty("database"), "database"), shardNumber));
        var rawOrigin = root.GetProperty("explicit_origin");
        var origin = rawOrigin.ValueKind == JsonValueKind.Null ? null : RetainedString(rawOrigin, "explicit_origin");
        if (origin is not null)
        {
            // Development is only restored through an explicitly configured development client.
            try
            {
                var canonical = NativeOrigin.Validate(origin, development: true).GetLeftPart(UriPartial.Authority);
                if (canonical != origin) throw new InvalidDataException("Retained origin must be canonical.");
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Retained origin is not a valid native origin.", exception);
            }
        }
        var requestBody = root.GetProperty("request_body").GetRawText();
        using var request = NativeCodec.Parse(requestBody);
        NativeCodec.DecodeCommit(scope, request.RootElement, out var identity);
        return new NativeRetainedOperation(scope, origin, identity, requestBody);
    }

    private static string RetainedString(JsonElement value, string name) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new InvalidDataException($"Retained {name} must be a string.");

    private static byte[] Encode(NativePreparedCommit prepared)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("local_version", 1);
            writer.WriteStartObject("scope");
            writer.WriteString("tenant", prepared.Scope.Tenant);
            writer.WriteString("database", prepared.Scope.Database);
            writer.WriteString("shard", prepared.Scope.Shard.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.WriteString("explicit_origin", prepared.ExplicitOrigin);
            writer.WritePropertyName("request_body");
            writer.WriteRawValue(prepared.RequestBody);
            writer.WriteEndObject();
        }
        if (output.Length > MaximumRetainedBytes) throw new InvalidDataException("Native operation file exceeds its byte limit.");
        return output.ToArray();
    }

    private static void Exact(JsonElement root, string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != names.Length ||
            names.Any(name => !root.TryGetProperty(name, out _)))
            throw new InvalidDataException("Local operation has missing or unknown fields.");
    }
}

/// <summary>Bounded status-only resolution of the original transaction; unresolved status never authorizes a fresh mutation.</summary>
public static class NativeRecovery
{
    /// <summary>Finite status polling only; unresolved outcomes never trigger a fresh or automatic commit.</summary>
    public static async Task<NativeRecoveryResult> ResolveAsync(NativeClient client, NativePreparedCommit prepared,
        TimeSpan deadline, TimeSpan pollInterval, CancellationToken cancellationToken = default)
    {
        client.RequirePrepared(prepared);
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(deadline));
        if (pollInterval < TimeSpan.FromMilliseconds(100) || pollInterval > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(deadline);
        var started = Stopwatch.GetTimestamp();
        var state = NativeTransactionState.Unknown;
        NativeFailure? failure = null;
        try
        {
            while (true)
            {
                budget.Token.ThrowIfCancellationRequested();
                var result = await client.TransactionStatusAsync(prepared.Identity, budget.Token).ConfigureAwait(false);
                TimeSpan? retryAfter = null;
                if (result is NativeResult<NativeTransactionStatus>.Success success)
                {
                    state = success.Value.State;
                    failure = null;
                    if (state is NativeTransactionState.Committed or NativeTransactionState.Rejected or NativeTransactionState.Expired)
                        return new NativeRecoveryResult(prepared.Identity, state, success.Value.Receipt, null);
                }
                else if (result is NativeResult<NativeTransactionStatus>.Failure failed)
                {
                    failure = failed.Error;
                    if (failure.Code == "cancelled" && budget.IsCancellationRequested)
                        budget.Token.ThrowIfCancellationRequested();
                    if (!Transient(failure)) return new NativeRecoveryResult(prepared.Identity, state, null, failure);
                    retryAfter = failure.RetryAfter;
                }
                var remaining = deadline - Stopwatch.GetElapsedTime(started);
                var delay = retryAfter > pollInterval ? retryAfter.Value : pollInterval;
                if (remaining <= TimeSpan.Zero)
                {
                    failure = new NativeFailure("deadline_exceeded", Transaction: prepared.Identity, MutationOutcome: NativeMutationOutcome.Uncertain);
                    break;
                }
                if (delay >= remaining)
                {
                    // No further request fits. Observe caller cancellation and
                    // the real deadline while the remaining budget elapses.
                    await Task.Delay(Timeout.InfiniteTimeSpan, budget.Token).ConfigureAwait(false);
                }
                await Task.Delay(delay, budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            failure = new NativeFailure(cancellationToken.IsCancellationRequested ? "cancelled" : "deadline_exceeded",
                Transaction: prepared.Identity, MutationOutcome: NativeMutationOutcome.Uncertain);
        }
        return new NativeRecoveryResult(prepared.Identity, state, null, failure);
    }

    private static bool Transient(NativeFailure failure) => failure.Code is "transport_unavailable" or "deadline_exceeded" or "discovery_unavailable" or "discovery_expired" ||
        failure.Status == 429 && failure.Code == "rate_limited" || failure.Status == 503 &&
        failure.Code is "capacity" or "storage_unavailable" or "authority_unavailable" or "commit_uncertain";
}
