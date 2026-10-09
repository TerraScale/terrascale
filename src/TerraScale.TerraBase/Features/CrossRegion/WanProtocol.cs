using Google.Protobuf;
using TerraBase.Features.CrossRegion.V1;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;

namespace TerraBase.Features.CrossRegion;

/// <summary>
/// Version 3 of cross-region replication (<c>wan.proto</c>, <c>docs/cross-region.md</c>): subjects in the cross-link
/// <c>query</c> family of ts-orchestrator's NATS fabric, and the identities on them.
/// </summary>
public static class WanProtocol
{
    /// <summary>Version of <see cref="WanRequest"/>.</summary>
    public const uint Version = 3;

    /// <summary>Largest request or reply: NATS's default <c>max_payload</c>, which leafnodes between regions keep.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>Most heads in one reply; the byte budget can end a page sooner.</summary>
    public const int MaxHeadsPerReply = 4096;

    // Room for the reply's framing around its batches.
    internal const int ReplyBudget = MaxMessageBytes - (64 * 1024);

    // Protect the entire inventory, including empty pages and membership, before it can authorize adoption or deletion.
    internal static byte[] EncodeHeads(HeadsReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.CalculateSize() is 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException("the decoded heads reply exceeds its size bounds");
        }

        var envelope = ApplicationEnvelope.Encode(reply.ToByteArray());
        return envelope.Length <= MaxMessageBytes ? envelope.ToArray()
            : throw new InvalidDataException("the encoded heads reply exceeds its size bound");
    }

    internal static HeadsReply DecodeHeads(ReadOnlyMemory<byte> data)
    {
        if (data.Length > MaxMessageBytes)
        {
            throw new InvalidDataException("the encoded heads reply exceeds its size bound");
        }

        var decoded = ApplicationEnvelope.Decode(data.Span, MaxMessageBytes);
        try
        {
            return HeadsReply.Parser.ParseFrom(decoded);
        }
        catch (InvalidProtocolBufferException error)
        {
            throw new InvalidDataException("the heads reply is not valid protobuf", error);
        }
    }

    /// <summary>The subject every member of <paramref name="cell"/> answers with the first page of its heads.</summary>
    public static string HeadsSubject(string cell) => $"ts.orch.v1.{cell}.query.terrabase-wan.heads";

    /// <summary>The subject of one member of <paramref name="cell"/>, by node name.</summary>
    public static string MemberSubject(string cell, string member) => $"ts.orch.v1.{cell}.query.terrabase-wan.member.{member}";

    /// <summary>Whether <paramref name="member"/> can name a subject: a node name of lowercase letters, digits and inner hyphens.</summary>
    public static bool IsMember(string member) =>
        member.Length is >= 1 and <= 63 && member[0] != '-' && member[^1] != '-' && member.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>The 48 bytes that order databases in heads: tenant, project and database ULIDs.</summary>
    internal static byte[] Key(TenantContext database) =>
        [.. Id(database.OrganizationId).Span, .. Id(database.ProjectId!).Span, .. Id(database.DatabaseId!).Span];

    internal static ByteString Id(string value) => ByteString.CopyFrom(Ulid.Parse(value).ToByteArray());

    /// <summary>The database of three 16-byte ULIDs, or <see langword="null"/> when one is malformed.</summary>
    internal static TenantContext? Database(ByteString tenant, ByteString project, ByteString database) =>
        tenant.Length == 16 && project.Length == 16 && database.Length == 16
            ? new TenantContext(Text(tenant), PlanTier.Hobby, Text(project), Text(database))
            : null;

    private static string Text(ByteString value) => new Ulid(value.Span).ToString();
}
