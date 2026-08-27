// <copyright file="Dto.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text.Json;
using TerraBase.Protocol;

/// <summary>The JSON surface of the typed API: DTOs over the TBEP bodies, <see cref="System.Text.Json"/> only.</summary>
public static class Dto
{
    /// <summary>Converts PascalCase variant names to their snake_case wire names.</summary>
    /// <param name="value">The variant name.</param>
    /// <returns>The snake_case name.</returns>
    public static string ToSnakeCase(this string value) =>
        string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}

/// <summary>The recursive document-body value on the JSON surface: exactly one discriminator set, mirroring the wire value.</summary>
public sealed record JsonValue
{
    /// <summary>The null discriminator.</summary>
    public bool? Null { get; init; }

    /// <summary>The boolean discriminator.</summary>
    public bool? Boolean { get; init; }

    /// <summary>The integer discriminator.</summary>
    public long? Integer { get; init; }

    /// <summary>The number discriminator.</summary>
    public double? Number { get; init; }

    /// <summary>The string discriminator.</summary>
    public string? String { get; init; }

    /// <summary>The array discriminator.</summary>
    public IReadOnlyList<JsonValue>? Array { get; init; }

    /// <summary>The object discriminator.</summary>
    public IReadOnlyDictionary<string, JsonValue>? Object { get; init; }

    /// <summary>Converts to the protocol's value.</summary>
    /// <returns>The <see cref="TbepValue"/>.</returns>
    public TbepValue ToValue() => this switch
    {
        { Null: not null } => TbepValue.Null,
        { Boolean: { } b } => TbepValue.Of(b),
        { Integer: { } i } => TbepValue.Of(i),
        { Number: { } n } => TbepValue.Of(n),
        { String: { } s } => TbepValue.Of(s),
        { Array: { } items } => TbepValue.Of(items.Select(item => item.ToValue()).ToArray()),
        { Object: { } fields } => TbepValue.Of(
            fields.Select(field => new ObjectField(field.Key, field.Value.ToValue())).ToArray()),
        _ => throw new JsonException("a value carries exactly one discriminator"),
    };

    /// <summary>Builds from the protocol's value.</summary>
    /// <param name="value">The <see cref="TbepValue"/>.</param>
    /// <returns>The JSON value.</returns>
    public static JsonValue Of(TbepValue value) => value.Tag switch
    {
        0 => new JsonValue { Null = true },
        1 => new JsonValue { Boolean = (bool)value.Payload! },
        2 => new JsonValue { Integer = (long)value.Payload! },
        3 => new JsonValue { Number = (double)value.Payload! },
        4 => new JsonValue { String = (string)value.Payload! },
        5 => new JsonValue { Array = ((TbepValue[])value.Payload!).Select(Of).ToArray() },
        6 => new JsonValue { Object = ((ObjectField[])value.Payload!).ToDictionary(field => field.Name, field => Of(field.Value)) },
        _ => throw new JsonException($"unknown value tag {value.Tag}"),
    };
}

/// <summary>One document body: the identity, the version, and the body.</summary>
public sealed record DocumentBodyDto(string Id, string Collection, ulong Version, JsonValue Body)
{
    /// <summary>Builds from the protocol body.</summary>
    /// <param name="body">The protocol body.</param>
    /// <returns>The DTO.</returns>
    public static DocumentBodyDto Of(DocumentBody body) => new(body.Id, body.Collection, body.Version, JsonValue.Of(body.Body));
}

/// <summary>The document read answer.</summary>
public sealed record DocumentReadDto(bool Found, DocumentBodyDto? Document);

/// <summary>The write or delete outcome.</summary>
public sealed record WriteOutcomeDto(string Outcome, CommitReceiptDto? Receipt = null, string? Reason = null)
{
    /// <summary>Builds from the protocol outcome.</summary>
    /// <param name="outcome">The protocol outcome.</param>
    /// <returns>The DTO.</returns>
    public static WriteOutcomeDto Of(WriteOutcome outcome) => outcome.Variant switch
    {
        "Committed" => new WriteOutcomeDto("committed", CommitReceiptDto.Of((CommitReceipt)outcome.Payload!)),
        "Conflict" => new WriteOutcomeDto("conflict"),
        "Rejected" => new WriteOutcomeDto("rejected", Reason: ((WriteReason)outcome.Payload!).Variant.ToSnakeCase()),
        _ => throw new JsonException($"unknown write outcome {outcome.Variant}"),
    };
}

/// <summary>One commit receipt.</summary>
public sealed record CommitReceiptDto(ulong Sequence, ulong Version, string OperationId)
{
    /// <summary>Builds from the protocol receipt.</summary>
    /// <param name="receipt">The protocol receipt.</param>
    /// <returns>The DTO.</returns>
    public static CommitReceiptDto Of(CommitReceipt receipt) => new(receipt.Sequence, receipt.Version, receipt.OperationId);
}

/// <summary>The query execution request.</summary>
public sealed record QueryRequestDto
{
    /// <summary>The query text (TQL).</summary>
    public required string Text { get; init; }

    /// <summary>The item bound; the default is 256.</summary>
    public uint? MaxItems { get; init; }
}

/// <summary>The query execution answer.</summary>
public sealed record QueryResponseDto(DocumentBodyDto[] Documents, uint Count, string Terminal);

/// <summary>The query-cancellation live signal (GAP-Q12, PB-4034).</summary>
public sealed record QueryCancelDto
{
    /// <summary>The in-flight query identity.</summary>
    public required ulong QueryId { get; init; }
}

/// <summary>The query-cancellation answer.</summary>
public sealed record QueryCancelResponseDto(string Outcome);

/// <summary>The typed family request: a codec name and its bytes.</summary>
public sealed record FamilyRequestDto
{
    /// <summary>The request codec identity.</summary>
    public required string RequestCodec { get; init; }

    /// <summary>The request bytes, base64.</summary>
    public required string Request { get; init; }

    /// <summary>The decoded request bytes.</summary>
    public byte[] RequestBytes => Convert.FromBase64String(this.Request);
}

/// <summary>The typed family answer.</summary>
public sealed record FamilyResponseDto(string ResponseCodec, byte[] Response);

/// <summary>The stream acknowledgement request.</summary>
public sealed record StreamAckDto
{
    /// <summary>The last sequence the consumer held.</summary>
    public required ulong ThroughSequence { get; init; }
}

/// <summary>The stream acknowledgement answer.</summary>
public sealed record StreamAckResponseDto(ulong SubscriptionId, ulong? AcknowledgedThrough);

/// <summary>The sync session setup request: a collection scope and an optional resume checkpoint.</summary>
public sealed record SyncSessionSetupDto
{
    /// <summary>The collection scope.</summary>
    public required string Collection { get; init; }

    /// <summary>The resume checkpoint bytes, base64, or <c>null</c> for a fresh session.</summary>
    public string? Resume { get; init; }

    /// <summary>The decoded resume checkpoint, or <c>null</c>.</summary>
    public byte[]? ResumeBytes => this.Resume is null ? null : Convert.FromBase64String(this.Resume);
}

/// <summary>The sync session setup answer.</summary>
public sealed record SyncSessionDto(ulong SessionId, byte[] Checkpoint, byte[] RetainedFloor);

/// <summary>One live sync session of the caller's tenant.</summary>
public sealed record SyncSessionSummaryDto(ulong SessionId, string Collection, int Generation);

/// <summary>The delta pull request.</summary>
public sealed record SyncDeltaDto
{
    /// <summary>The checkpoint bytes to serve after, base64.</summary>
    public required string From { get; init; }

    /// <summary>The decoded checkpoint bytes.</summary>
    public byte[] FromBytes => Convert.FromBase64String(this.From);
}

/// <summary>The delta pull answer.</summary>
public sealed record SyncDeltaResponseDto(
    ulong SessionId,
    string PayloadCodec,
    byte[] Payload,
    byte[] Next,
    uint Commits,
    bool SingleCommitOversize);

/// <summary>The snapshot pull answer.</summary>
public sealed record SyncSnapshotResponseDto(ulong SessionId, string PayloadCodec, byte[] Payload, byte[] Checkpoint);

/// <summary>The checkpoint acknowledgement request.</summary>
public sealed record SyncAckDto
{
    /// <summary>The durable checkpoint bytes, base64.</summary>
    public required string Checkpoint { get; init; }

    /// <summary>The decoded checkpoint bytes.</summary>
    public byte[] CheckpointBytes => Convert.FromBase64String(this.Checkpoint);
}

/// <summary>The acknowledged answer.</summary>
public sealed record SyncAckedDto(ulong SessionId);

/// <summary>The server-ack mutation request (PD-2026-08-26-12 D2: no optimistic patches).</summary>
public sealed record SyncMutationDto
{
    /// <summary>The document id.</summary>
    public required string Document { get; init; }

    /// <summary>The change.</summary>
    public required SyncChangeDto Change { get; init; }
}

/// <summary>The change of a server-ack mutation.</summary>
public sealed record SyncChangeDto
{
    /// <summary>The put fields.</summary>
    public IReadOnlyList<SyncFieldDto>? Put { get; init; }

    /// <summary>The delete flag.</summary>
    public bool? Delete { get; init; }

    /// <summary>Converts to the protocol change.</summary>
    /// <returns>The <see cref="SyncChange"/>.</returns>
    public SyncChange ToSyncChange() => this.Delete is true
        ? SyncChange.Delete()
        : SyncChange.Put((this.Put ?? []).Select(field => new SyncField { Name = field.Name, Value = field.Value.ToValue() }).ToArray());
}

/// <summary>One field of a put change.</summary>
public sealed record SyncFieldDto(string Name, JsonValue Value);

/// <summary>The mutation outcome answer.</summary>
public sealed record SyncMutationResponseDto(ulong SessionId, SyncMutationOutcomeDto Outcome);

/// <summary>The server-ack mutation outcome.</summary>
public sealed record SyncMutationOutcomeDto(string Outcome, CommitReceiptDto? Receipt = null, string? Reason = null)
{
    /// <summary>Builds from the protocol outcome.</summary>
    /// <param name="outcome">The protocol outcome.</param>
    /// <returns>The DTO.</returns>
    public static SyncMutationOutcomeDto Of(MutationOutcome outcome) => outcome.Variant switch
    {
        "Applied" => new SyncMutationOutcomeDto("applied", CommitReceiptDto.Of((CommitReceipt)outcome.Payload!)),
        "Rejected" => new SyncMutationOutcomeDto("rejected", Reason: ((MutationReason)outcome.Payload!).Variant.ToSnakeCase()),
        _ => throw new JsonException($"unknown mutation outcome {outcome.Variant}"),
    };
}

/// <summary>The engine shutdown request.</summary>
public sealed record ShutdownRequestDto
{
    /// <summary>The shutdown reason: <c>operator</c> (default) or <c>supervisor</c>.</summary>
    public string? Reason { get; init; }
}

/// <summary>The engine shutdown answer.</summary>
public sealed record ShutdownResponseDto(string State, uint InFlight);

/// <summary>The composite health answer; never optimistic.</summary>
public sealed record HealthResponse(string Status, EngineHealth Engine);

/// <summary>The engine half of the composite health.</summary>
public sealed record EngineHealth(
    string? State,
    uint? Sessions,
    uint? Subscriptions,
    ulong? FramesServed,
    int Generation,
    int Restarts,
    string? Detail);

/// <summary>The host metrics answer.</summary>
public sealed record MetricsResponse(
    int EngineGeneration,
    int EngineRestarts,
    string EngineStatus,
    int OpenStreams,
    long DroppedFrames,
    int TrackedSessions,
    int AuditRecords);

/// <summary>The audit answer.</summary>
public sealed record AuditResponse(IReadOnlyList<AuditRecord> Records);

/// <summary>The typed error answer.</summary>
public sealed record ErrorDto(string Code, string Detail)
{
    /// <summary>Set on a checkpoint refusal: the consumer resyncs by pulling a snapshot.</summary>
    public bool? ResyncRequired { get; init; }
}
