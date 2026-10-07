using Google.Protobuf;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>
/// Protocol 1.0 of the <c>Replication.Sync</c> stream between TerraBase nodes. One stream carries the
/// change groups of one database from one origin: <c>Hello</c> names its organization, project and database
/// by their 16 UUID bytes in RFC order, and the origin (its 16 bytes in <c>member_id</c> and <c>member_incarnation</c>);
/// <c>Welcome</c> reports the receiver's applied prefix of that origin, and the sender resumes after it.
/// Every group travels in an LZ4 <see cref="ApplicationEnvelope"/>; gRPC transport compression stays off.
/// </summary>
public static class ReplicationProtocol
{
    /// <summary>Largest gRPC message in either direction: one application batch plus Protobuf framing.</summary>
    public const int MaxMessageBytes = MaxApplicationBytes + (64 * 1024);

    /// <summary>Largest envelope and largest decoded change group.</summary>
    public const int MaxApplicationBytes = 4 * 1024 * 1024;

    /// <summary>Format version of change groups, events and the merge policy (<see cref="ChangeGroup"/>).</summary>
    public const uint FormatVersion = 1;

    /// <summary>Most batches a sender has sent but not seen durably acknowledged.</summary>
    public const uint MaxInFlightBatches = 8;

    /// <summary>Most envelope bytes a sender has sent but not seen durably acknowledged.</summary>
    public const ulong ByteCredit = 16 * 1024 * 1024;

    /// <summary>Most Sync streams one node serves at a time.</summary>
    public const uint MaxOpenStreams = 16;

    /// <summary>
    /// The membership epoch of every node until certified membership exists (TS-469); the configuration id is
    /// empty. Hello, Welcome, batches and acknowledgments must all carry it.
    /// </summary>
    public const ulong MembershipEpoch = 0;

    private const uint Major = 1;

    /// <summary>A receiver ends a stream on which nothing arrives for this long.</summary>
    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>A sender abandons a stream on which nothing it waits for arrives for this long.</summary>
    public static TimeSpan ProgressTimeout { get; } = TimeSpan.FromSeconds(15);

    internal static TransferLimits Limits => new()
    {
        MaxEncodedApplicationBytes = MaxApplicationBytes,
        MaxDecodedApplicationBytes = MaxApplicationBytes,
        MaxTransactionGroupBytes = IStorageTransaction.MaxChangeGroupBytes,
        MaxChangesPerGroup = IStorageTransaction.MaxChangeGroupChanges,
        MaxGrpcMessageBytes = MaxMessageBytes,
        MaxInFlightBatches = MaxInFlightBatches,
        MaxOpenStreams = MaxOpenStreams,
        ByteCredit = ByteCredit,
    };

    /// <summary>Opens a stream for the change groups of <paramref name="origin"/> in <paramref name="database"/>.</summary>
    public static Hello CreateHello(TenantContext database, ReadOnlyMemory<byte> origin, string region = "")
    {
        var range = new VersionRange { Minimum = new ProtocolVersion { Major = Major }, Maximum = new ProtocolVersion { Major = Major } };
        var formats = new VersionRange { Minimum = new ProtocolVersion { Major = FormatVersion }, Maximum = new ProtocolVersion { Major = FormatVersion } };
        return new Hello
        {
            LocalIdentity = Member(database, origin, region),
            ProtocolVersions = range,
            EventFormatVersions = formats,
            RecordFormatVersions = formats,
            Codecs = { PayloadCodec.Lz4BlockV1 },
            Limits = Limits,
            MinimumMergePolicyVersion = FormatVersion,
            MaximumMergePolicyVersion = FormatVersion,
        };
    }

    /// <summary>Wraps change group <paramref name="sequence"/> of <paramref name="origin"/> in an LZ4 envelope.</summary>
    public static CommittedGroupBatch CreateBatch(TenantContext database, ReadOnlyMemory<byte> origin, long sequence, IReadOnlyList<DocumentChange> changes, ReadOnlyMemory<byte> previousDigest = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (previousDigest.IsEmpty)
        {
            previousDigest = sequence == 1 ? JournalDigest.Initial(origin.Span)
                : throw new ArgumentException("a later group requires its preceding journal digest", nameof(previousDigest));
        }

        var plain = ChangeGroup.Encode(changes);
        var envelope = ApplicationEnvelope.Encode(plain);
        return new CommittedGroupBatch
        {
            PreviousJournalDigest = ByteString.CopyFrom(previousDigest.Span),
            JournalDigest = ByteString.CopyFrom(Engine.JournalDigest.Compute(origin.Span, sequence, previousDigest.Span, changes)),
            ProtocolMajor = Major,
            EventFormatVersion = FormatVersion,
            RecordFormatVersion = FormatVersion,
            MergePolicyVersion = FormatVersion,
            MembershipEpoch = MembershipEpoch,
            EventId = new CommittedEventId
            {
                TenantId = Id(database.OrganizationId),
                ProjectId = Id(database.ProjectId!),
                DatabaseId = Id(database.DatabaseId!),
                Origin = Origin(origin),
                Sequence = (ulong)sequence,
            },
            Codec = PayloadCodec.Lz4BlockV1,
            WalGroupBytes = (uint)plain.Length,
            ChangeCount = (uint)changes.Count,
            EncodedApplicationBytes = (uint)envelope.Length,
            DecodedApplicationBytes = (uint)plain.Length,
            ApplicationEnvelope = UnsafeByteOperations.UnsafeWrap(envelope),
        };
    }

    /// <summary>
    /// A database's identity on a stream: its tenant-qualified UUIDs, and its origin bytes as both member id and incarnation
    /// (no node-level member id exists before membership), in <see cref="MembershipEpoch"/>.
    /// </summary>
    internal static PeerIdentity Member(TenantContext database, ReadOnlyMemory<byte> origin, string region = "") => new()
    {
        TenantId = Id(database.OrganizationId),
        ProjectId = Id(database.ProjectId!),
        DatabaseId = Id(database.DatabaseId!),
        MemberId = ByteString.CopyFrom(origin.Span),
        MemberIncarnation = ByteString.CopyFrom(origin.Span),
        MembershipEpoch = MembershipEpoch,
        Region = region,
    };

    /// <summary>Whether <paramref name="identity"/> is a well-formed <see cref="Member"/> of <paramref name="database"/>.</summary>
    internal static bool IsMember(PeerIdentity? identity, TenantContext database) =>
        identity is not null && Matches(identity, database) && identity.ShardId == 0
        && identity.MemberId.Length == 16 && identity.MemberId.Equals(identity.MemberIncarnation)
        && identity.MembershipEpoch == MembershipEpoch && identity.ConfigurationId.IsEmpty
        && (identity.Region.Length == 0 || TerraBaseOptions.IsRegion(identity.Region));

    internal static OriginIdentity Origin(ReadOnlyMemory<byte> origin) =>
        new() { ReplicaId = ByteString.CopyFrom(origin.Span), Incarnation = ByteString.CopyFrom(origin.Span) };

    /// <summary>
    /// The database, origin and negotiated limits a valid <paramref name="hello"/> opens, or a reason to refuse it.
    /// Protocol 1.0 and format 1 must be in the offered ranges; the identity must be a <see cref="Member"/> in
    /// <see cref="MembershipEpoch"/>; every advertised limit is positive and the result is the minimum of both sides.
    /// </summary>
    internal static StreamErrorCode Accept(Hello hello, out TenantContext database, out ReadOnlyMemory<byte> origin, out TransferLimits limits)
    {
        database = null!;
        origin = hello.LocalIdentity?.MemberIncarnation.Memory ?? default;
        limits = Negotiate(Limits, hello.Limits);
        if (!Includes(hello.ProtocolVersions, Major) || !Includes(hello.EventFormatVersions, FormatVersion)
            || !Includes(hello.RecordFormatVersions, FormatVersion) || !hello.Codecs.Contains(PayloadCodec.Lz4BlockV1)
            || hello.MinimumMergePolicyVersion > FormatVersion || hello.MaximumMergePolicyVersion < FormatVersion
            || !Usable(limits))
        {
            return StreamErrorCode.StreamErrorIncompatibleVersion;
        }

        if (hello.LocalIdentity is { } identity && (identity.MembershipEpoch != MembershipEpoch || !identity.ConfigurationId.IsEmpty))
        {
            return StreamErrorCode.StreamErrorStaleEpoch;
        }

        var member = hello.LocalIdentity;
        if (member is null || member.TenantId.Length != 16 || member.ProjectId.Length != 16 || member.DatabaseId.Length != 16)
        {
            return StreamErrorCode.StreamErrorUnsupportedFormat;
        }

        database = new TenantContext(Text(member.TenantId), PlanTier.Hobby, Text(member.ProjectId), Text(member.DatabaseId));
        return IsMember(member, database) ? StreamErrorCode.StreamErrorUnspecified : StreamErrorCode.StreamErrorUnsupportedFormat;
    }

    /// <summary>Each limit of <paramref name="ours"/> lowered to <paramref name="theirs"/>; a missing or zero limit stays zero.</summary>
    internal static TransferLimits Negotiate(TransferLimits ours, TransferLimits? theirs) => theirs is null ? new TransferLimits() : new()
    {
        MaxEncodedApplicationBytes = Math.Min(ours.MaxEncodedApplicationBytes, theirs.MaxEncodedApplicationBytes),
        MaxDecodedApplicationBytes = Math.Min(ours.MaxDecodedApplicationBytes, theirs.MaxDecodedApplicationBytes),
        MaxTransactionGroupBytes = Math.Min(ours.MaxTransactionGroupBytes, theirs.MaxTransactionGroupBytes),
        MaxEventMetadataBytes = Math.Min(ours.MaxEventMetadataBytes, theirs.MaxEventMetadataBytes),
        MaxChangesPerGroup = Math.Min(ours.MaxChangesPerGroup, theirs.MaxChangesPerGroup),
        MaxGrpcMessageBytes = Math.Min(ours.MaxGrpcMessageBytes, theirs.MaxGrpcMessageBytes),
        MaxInFlightBatches = Math.Min(ours.MaxInFlightBatches, theirs.MaxInFlightBatches),
        MaxOpenStreams = Math.Min(ours.MaxOpenStreams, theirs.MaxOpenStreams),
        ByteCredit = Math.Min(ours.ByteCredit, theirs.ByteCredit),
    };

    /// <summary>Whether every limit a stream depends on is positive (event metadata is not used).</summary>
    internal static bool Usable(TransferLimits? limits) =>
        limits is
        {
            MaxEncodedApplicationBytes: > 0, MaxDecodedApplicationBytes: > 0, MaxTransactionGroupBytes: > 0, MaxChangesPerGroup: > 0,
            MaxGrpcMessageBytes: > 0, MaxInFlightBatches: > 0, MaxOpenStreams: > 0, ByteCredit: > 0
        };

    /// <summary>
    /// Checks one batch, as the <see cref="SyncRequest"/> that carries it, against the negotiated
    /// <paramref name="limits"/>: the sender before it writes the batch and the receiver before it decodes it run
    /// this same check, so neither side can exceed what the other agreed to. <paramref name="contentBytes"/> are the
    /// group's collection, id and JSON bytes when the caller knows them (the sender); the receiver enforces that
    /// limit while decoding.
    /// </summary>
    /// <exception cref="InvalidDataException">The batch exceeds a negotiated limit.</exception>
    internal static void CheckLimits(SyncRequest request, TransferLimits limits, long? contentBytes = null)
    {
        var batch = request.CommittedGroup;
        var exceeded =
            request.CalculateSize() > limits.MaxGrpcMessageBytes ? "gRPC message size"
            : batch.EncodedApplicationBytes > limits.MaxEncodedApplicationBytes ? "encoded application bytes"
            : batch.DecodedApplicationBytes > limits.MaxDecodedApplicationBytes ? "decoded application bytes"
            : batch.ChangeCount > limits.MaxChangesPerGroup ? "changes per group"
            : batch.EncodedApplicationBytes > limits.ByteCredit ? "byte credit"
            : contentBytes > limits.MaxTransactionGroupBytes ? "transaction group bytes"
            : null;
        if (exceeded is not null)
        {
            throw new InvalidDataException($"change group {batch.EventId?.Sequence} exceeds the negotiated {exceeded}");
        }
    }

    /// <summary>
    /// Validates a batch of the stream's database and origin against the negotiated <paramref name="limits"/>
    /// (<see cref="CheckLimits"/>) and decodes its change group.
    /// </summary>
    /// <exception cref="NotSupportedException">The batch uses another protocol, format or codec.</exception>
    /// <exception cref="InvalidDataException">The batch or its envelope is invalid, oversized or corrupt.</exception>
    internal static List<DocumentChange> Read(SyncRequest request, TenantContext database, ReadOnlyMemory<byte> origin, TransferLimits limits)
    {
        var batch = request.CommittedGroup;
        if (batch.ProtocolMajor != Major || batch.EventFormatVersion != FormatVersion || batch.RecordFormatVersion != FormatVersion
            || batch.MergePolicyVersion != FormatVersion || batch.Codec != PayloadCodec.Lz4BlockV1)
        {
            throw new NotSupportedException("the batch uses an unsupported protocol, format or codec");
        }

        CheckLimits(request, limits);
        var id = batch.EventId;
        if (id is null || !Matches(id, database) || id.ShardId != 0 || id.Sequence is 0 or > long.MaxValue
            || id.Origin is null || !id.Origin.Incarnation.Span.SequenceEqual(origin.Span) || !id.Origin.ReplicaId.Span.SequenceEqual(origin.Span)
            || batch.EncodedApplicationBytes != batch.ApplicationEnvelope.Length
            || batch.WalGroupBytes != batch.DecodedApplicationBytes
            || batch.MembershipEpoch != MembershipEpoch || !batch.ConfigurationId.IsEmpty
            || batch.EventMetadataBytes != 0 || !batch.DistributedMetadata.IsEmpty
            || batch.PreviousJournalDigest.Length != Engine.JournalDigest.Length || batch.JournalDigest.Length != Engine.JournalDigest.Length)
        {
            throw new InvalidDataException("the batch identity or declared sizes are invalid");
        }

        var plain = ApplicationEnvelope.Decode(batch.ApplicationEnvelope.Span, (int)batch.DecodedApplicationBytes);
        var changes = ChangeGroup.Decode(plain, (int)limits.MaxChangesPerGroup, (int)limits.MaxTransactionGroupBytes);
        return plain.Length == batch.DecodedApplicationBytes && changes.Count == batch.ChangeCount
            && batch.JournalDigest.Span.SequenceEqual(Engine.JournalDigest.Compute(origin.Span, (long)id.Sequence, batch.PreviousJournalDigest.Span, changes))
            ? changes
            : throw new InvalidDataException("the batch does not match its declared sizes");
    }

    /// <summary>
    /// Whether a <paramref name="welcome"/> answers <see cref="CreateHello"/> for <paramref name="database"/>: protocol 1.0,
    /// format and merge policy 1, LZ4, a <see cref="Member"/> identity in this epoch, and limits that are exactly the
    /// minimum of ours and its own (a peer cannot raise a limit we offered).
    /// </summary>
    internal static bool Accepts(Welcome welcome, TenantContext database) =>
        welcome.ProtocolVersion is { Major: Major, Minor: 0 }
        && welcome.EventFormatVersion == FormatVersion && welcome.RecordFormatVersion == FormatVersion
        && welcome.MergePolicyVersion == FormatVersion && welcome.Codecs.Contains(PayloadCodec.Lz4BlockV1)
        && IsMember(welcome.LocalIdentity, database)
        && Usable(welcome.Limits) && Negotiate(Limits, welcome.Limits).Equals(welcome.Limits);

    internal static bool Matches(CommittedEventId identity, TenantContext database) =>
        identity.TenantId.Equals(Id(database.OrganizationId)) && identity.ProjectId.Equals(Id(database.ProjectId!))
        && identity.DatabaseId.Equals(Id(database.DatabaseId!));

    private static bool Matches(PeerIdentity identity, TenantContext database) =>
        identity.TenantId.Equals(Id(database.OrganizationId)) && identity.ProjectId.Equals(Id(database.ProjectId!))
        && identity.DatabaseId.Equals(Id(database.DatabaseId!));

    private static ByteString Id(string value) => ByteString.CopyFrom(Guid.ParseExact(value, "D").ToByteArray(bigEndian: true));

    private static string Text(ByteString value) => new Guid(value.Span, bigEndian: true).ToString("D");

    // Complete (major, minor) versions: a range of 1.1 to 1.2 does not include 1.0.
    private static bool Includes(VersionRange? range, uint major) =>
        range?.Minimum is { } minimum && range.Maximum is { } maximum
        && (minimum.Major, minimum.Minor).CompareTo((major, 0u)) <= 0 && (major, 0u).CompareTo((maximum.Major, maximum.Minor)) <= 0;
}
