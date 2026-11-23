using TerraScale.Contracts.Consistency;

namespace TerraScale.Contracts.Http;

public sealed record AuthorizeRequest(string Project, string? Database, IReadOnlyList<string> Scopes);

public sealed record CallerGrant(
    Ulid OrganizationId,
    Ulid ProjectId,
    Ulid? DatabaseId,
    string? DatabaseName,
    bool EndUser,
    bool Anonymous,
    bool DatabaseRestricted,
    string Subject,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Scopes);

public sealed record ChangefeedHttpItem(
    Ulid DatabaseId,
    Ulid TxId,
    string OriginRegion,
    long Sequence,
    ulong SchemaEpoch,
    ConsistencyMode Mode,
    DateTimeOffset CommittedAtUtc,
    string Bookmark);

public static class PlatformHeaders
{
    public const string CallerPeer = "X-TerraScale-Caller-Peer";

    public const string Changefeed = "application/x-ndjson";
}
