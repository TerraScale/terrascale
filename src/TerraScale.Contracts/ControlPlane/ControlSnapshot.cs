using System.Text.Json.Serialization;
using TerraScale.Contracts.Consistency;

namespace TerraScale.Contracts.ControlPlane;

public sealed record ControlSnapshot(
    long Version,
    DateTimeOffset IssuedAtUtc,
    string Signature,
    IReadOnlyList<ControlOrganization> Organizations,
    IReadOnlyList<ControlProject> Projects,
    IReadOnlyList<ControlDatabase> Databases,
    IReadOnlyList<ControlApiKeyVerifier> ApiKeys,
    IReadOnlyList<ControlRegion> Regions);

public enum ControlDatabaseState
{
    [JsonStringEnumMemberName("active")]
    Active,
    [JsonStringEnumMemberName("suspended")]
    Suspended,
    [JsonStringEnumMemberName("deleted")]
    Deleted,
}

public sealed record ControlOrganization(Ulid Id, string Name, string Plan, bool Suspended);

public sealed record ControlProject(Ulid Id, Ulid OrganizationId, string Name);

public sealed record ControlDatabase(
    Ulid Id,
    Ulid ProjectId,
    string Name,
    IReadOnlyList<string> Regions,
    ConsistencyMode DefaultConsistency,
    bool Strict,
    ControlDatabaseState State,
    long StorageLimitBytes,
    string WrappedDataKey,
    string DataKeyId,
    DateTimeOffset CreatedAtUtc);

public sealed record ControlApiKeyVerifier(
    string KeyId,
    Ulid ProjectId,
    Ulid? DatabaseId,
    string VerifierHash,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAtUtc);

public sealed record ControlRegion(string Id, string Name, string PublicHost, bool AcceptingWrites);

public sealed record ControlSnapshotAck(long Version, string NodeId, string Region, DateTimeOffset AppliedAtUtc);
