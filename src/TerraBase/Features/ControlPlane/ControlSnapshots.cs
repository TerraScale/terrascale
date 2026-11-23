using System.Buffers;
using System.Buffers.Text;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Configuration;
using TerraScale.Contracts.ControlPlane;

namespace TerraBase.Features.ControlPlane;

internal sealed record AcceptedSnapshot(ControlSnapshot Snapshot, bool Changed);

internal sealed class ControlSnapshots(TerraBaseOptions options, TimeProvider time)
{
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private Index _index = Index.Empty;

    public long Version => Volatile.Read(ref _index).Snapshot?.Version ?? 0;

    public DateTimeOffset? IssuedAt => Volatile.Read(ref _index).Snapshot?.IssuedAtUtc;

    public ControlSnapshot? Current => Volatile.Read(ref _index).Snapshot;

    public ControlApiKeyVerifier? FindApiKey(string verifierHash) =>
        Volatile.Read(ref _index).ApiKeys.GetValueOrDefault(verifierHash);

    public Ulid? OrganizationOf(Ulid projectId) =>
        Volatile.Read(ref _index).Projects.TryGetValue(projectId, out var project) ? project.OrganizationId : null;

    public ControlRegion? Region(string id) => Volatile.Read(ref _index).Regions.GetValueOrDefault(id);

    public IReadOnlyList<ControlRegion> Regions => Volatile.Read(ref _index).Snapshot?.Regions ?? [];

    public Result<AcceptedSnapshot, Validation, Unauthorized, Conflict, Unavailable> Accept(ControlSnapshot snapshot, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (options.ControlSnapshotKey is not { } key)
        {
            return new Unavailable("TERRABASE_CONTROL_KEY_FILE is not configured; control snapshots cannot be verified", "control_key_missing");
        }

        if (snapshot.Version <= 0 || snapshot.Organizations is null || snapshot.Projects is null || snapshot.Databases is null
            || snapshot.ApiKeys is null || snapshot.Regions is null)
        {
            return new Validation("the control snapshot is incomplete", "invalid_snapshot");
        }

        if (snapshot.IssuedAtUtc - time.GetUtcNow() > MaxSkew)
        {
            return new Validation("the control snapshot is issued in the future", "invalid_snapshot");
        }

        if (!Verify(key, snapshot.Signature, body.Span))
        {
            return new Unauthorized("the control snapshot signature does not verify", "invalid_signature");
        }

        lock (_gate)
        {
            if (_index.Snapshot is { } current && current.Version > snapshot.Version)
            {
                return new Conflict("a newer control snapshot is already applied", "stale_snapshot");
            }

            var changed = _index.Snapshot?.Version != snapshot.Version;
            Volatile.Write(ref _index, Index.Of(snapshot));
            return new AcceptedSnapshot(snapshot, changed);
        }
    }

    public static byte[]? SignedPayload(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(body);
        while (reader.Read())
        {
            if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("signature"u8) && reader.Read()
                && reader.TokenType == JsonTokenType.String)
            {
                var start = (int)reader.TokenStartIndex;
                var end = (int)reader.BytesConsumed;
                return [.. body[..start], .. "\"\""u8, .. body[end..]];
            }
        }

        return null;
    }

    private static bool Verify(ECDsa key, string encoded, ReadOnlySpan<byte> body)
    {
        var signature = new byte[Base64Url.GetMaxDecodedLength(encoded.Length)];
        if (encoded.Length is 0 or > 256 || Base64Url.DecodeFromChars(encoded, signature, out _, out var written) != OperationStatus.Done
            || SignedPayload(body) is not { } payload)
        {
            return false;
        }

        return key.VerifyData(payload, signature.AsSpan(0, written), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private sealed record Index(
        ControlSnapshot? Snapshot,
        FrozenDictionary<string, ControlApiKeyVerifier> ApiKeys,
        FrozenDictionary<Ulid, ControlProject> Projects,
        FrozenDictionary<string, ControlRegion> Regions)
    {
        public static Index Empty { get; } = new(null, FrozenDictionary<string, ControlApiKeyVerifier>.Empty, FrozenDictionary<Ulid, ControlProject>.Empty,
            FrozenDictionary<string, ControlRegion>.Empty);

        public static Index Of(ControlSnapshot snapshot) => new(
            snapshot,
            snapshot.ApiKeys.DistinctBy(static key => key.VerifierHash, StringComparer.Ordinal).ToFrozenDictionary(static key => key.VerifierHash, StringComparer.Ordinal),
            snapshot.Projects.DistinctBy(static project => project.Id).ToFrozenDictionary(static project => project.Id),
            snapshot.Regions.DistinctBy(static region => region.Id, StringComparer.Ordinal).ToFrozenDictionary(static region => region.Id, StringComparer.Ordinal));
    }
}
