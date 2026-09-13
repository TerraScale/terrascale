// <copyright file="Dto.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text.Json;
using System.Text.Json.Serialization;
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Integer is the documented JSON discriminator name.")]
    public long? Integer { get; init; }

    /// <summary>The number discriminator.</summary>
    public double? Number { get; init; }

    /// <summary>The string discriminator.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "String is the documented JSON discriminator name.")]
    public string? String { get; init; }

    /// <summary>The array discriminator.</summary>
    public IReadOnlyList<JsonValue>? Array { get; init; }

    /// <summary>The object discriminator.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Object is the documented JSON discriminator name.")]
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
    /// <summary>
    /// Set on a checkpoint refusal: the consumer resyncs by pulling a
    /// snapshot. Serialized only when demanded — a plain typed refusal
    /// omits the member instead of stating a null marker, so null-unsafe
    /// refusal parsers never see one (the #93 cross-repo defect).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ResyncRequired { get; init; }
}

// ---------------------------------------------------------------------------
// The schema surface (SM-04): typed schema-lifecycle DTOs. The schema
// document and the validated document arrive as ordinary JSON and convert
// to the protocol's typed value at the boundary — never as bytes, never as
// an opaque codec payload, so the closed keyword set of the documented
// JSON Schema subset is the only shape the surface accepts.
// ---------------------------------------------------------------------------

/// <summary>Conversions between plain JSON elements and the protocol's typed value.</summary>
public static class JsonValues
{
    /// <summary>Converts one plain JSON element to the protocol's typed value.</summary>
    /// <param name="element">The JSON element.</param>
    /// <returns>The <see cref="TbepValue"/>.</returns>
    /// <exception cref="JsonException">The element is not a value the protocol carries.</exception>
    public static TbepValue ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => TbepValue.Null,
        JsonValueKind.True => TbepValue.Of(true),
        JsonValueKind.False => TbepValue.Of(false),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => TbepValue.Of(integer),
        JsonValueKind.Number => TbepValue.Of(element.GetDouble()),
        JsonValueKind.String => TbepValue.Of(element.GetString()!),
        JsonValueKind.Array => TbepValue.Of(element.EnumerateArray().Select(ToValue).ToArray()),
        JsonValueKind.Object => TbepValue.Of(
            element.EnumerateObject().Select(property => new ObjectField(property.Name, ToValue(property.Value))).ToArray()),
        _ => throw new JsonException($"a JSON kind the protocol does not carry: {element.ValueKind}"),
    };

    /// <summary>Converts the protocol's typed value to a serializable JSON object.</summary>
    /// <param name="value">The <see cref="TbepValue"/>.</param>
    /// <returns>A boxed JSON value.</returns>
    /// <exception cref="JsonException">The value carries an unknown tag.</exception>
    public static object? ToJson(TbepValue value) => value.Tag switch
    {
        0 => null,
        1 => (bool)value.Payload!,
        2 => (long)value.Payload!,
        3 => (double)value.Payload!,
        4 => (string)value.Payload!,
        5 => ((TbepValue[])value.Payload!).Select(ToJson).ToArray(),
        6 => ((ObjectField[])value.Payload!).ToDictionary(field => field.Name, field => ToJson(field.Value)),
        _ => throw new JsonException($"unknown value tag {value.Tag}"),
    };
}

/// <summary>The schema declaration request: the typed schema document, or <c>null</c> to clear the policy.</summary>
public sealed record SchemaDeclareDto
{
    /// <summary>The schema document of the documented JSON Schema subset.</summary>
    public JsonElement? Schema { get; init; }
}

/// <summary>The recorded schema state.</summary>
public sealed record SchemaStatusDto(ulong? EnforcedVersion, uint RuleCount);

/// <summary>The on-demand validation request: the document to validate.</summary>
public sealed record SchemaValidateDto
{
    /// <summary>
    /// The document value. Optional at the binding level so the local gate
    /// runs before the request's own shape is judged (the same ordering the
    /// document endpoints keep).
    /// </summary>
    public JsonElement? Document { get; init; }
}

/// <summary>The validation answer: the enforced version and the typed violations, never a field value.</summary>
public sealed record SchemaValidationDto(ulong? EnforcedVersion, bool Valid, ViolationDto[] Violations);

/// <summary>One typed policy violation of one document field.</summary>
/// <param name="Field">The violated field name, or empty when the document itself is the violation.</param>
/// <param name="Kind">The snake_case violation kind.</param>
public sealed record ViolationDto(string Field, string Kind);

/// <summary>The migration state of one collection.</summary>
public sealed record MigrationStatusDto(MigrationStateDto State);

/// <summary>The observable state of one Schema Migration drive.</summary>
/// <param name="Kind">The snake_case state kind: none, in_progress, rolling_back, or complete.</param>
/// <param name="Manifest">The manifest digest of the driving contract.</param>
/// <param name="FromVersion">The from-version.</param>
/// <param name="ToVersion">The to-version.</param>
/// <param name="Documents">The documents scanned so far.</param>
/// <param name="Transformed">The documents transformed so far.</param>
/// <param name="Pages">The pages committed so far.</param>
public sealed record MigrationStateDto(
    string Kind,
    string Manifest,
    ulong FromVersion,
    ulong ToVersion,
    ulong Documents,
    ulong Transformed,
    ulong Pages)
{
    /// <summary>Builds from the protocol state.</summary>
    /// <param name="state">The protocol state.</param>
    /// <returns>The DTO.</returns>
    public static MigrationStateDto Of(MigrationState state) => new(
        state.Kind.Variant.ToSnakeCase(),
        state.Manifest,
        state.FromVersion,
        state.ToVersion,
        state.Documents,
        state.Transformed,
        state.Pages);
}

/// <summary>The dry-run request: the typed migration contract.</summary>
public sealed record MigrationDryRunDto
{
    /// <summary>
    /// The migration contract. Optional at the binding level so the local
    /// gate runs before the request's own shape is judged.
    /// </summary>
    public MigrationContractDto? Contract { get; init; }
}

/// <summary>The apply request: the typed migration contract and the explicit acknowledgment.</summary>
public sealed record MigrationApplyDto
{
    /// <summary>
    /// The migration contract. Optional at the binding level so the local
    /// gate runs before the request's own shape is judged.
    /// </summary>
    public MigrationContractDto? Contract { get; init; }

    /// <summary>The explicit not-rollbackable acknowledgment.</summary>
    public bool AcknowledgeNotRollbackable { get; init; }
}

/// <summary>The migration contract on the JSON surface.</summary>
public sealed record MigrationContractDto
{
    /// <summary>The collection.</summary>
    public required string Collection { get; init; }

    /// <summary>The from-version.</summary>
    public ulong FromVersion { get; init; }

    /// <summary>The to-version.</summary>
    public ulong ToVersion { get; init; }

    /// <summary>The target schema document.</summary>
    public required JsonElement Target { get; init; }

    /// <summary>The closed transform list.</summary>
    public IReadOnlyList<TransformDto> Transforms { get; init; } = [];

    /// <summary>Builds the protocol contract.</summary>
    /// <returns>The <see cref="SchemaMigrationContract"/>.</returns>
    /// <exception cref="JsonException">A transform names a kind or literal the closed set does not carry.</exception>
    public SchemaMigrationContract ToContract() => new()
    {
        Collection = this.Collection,
        FromVersion = this.FromVersion,
        ToVersion = this.ToVersion,
        Target = JsonValues.ToValue(this.Target),
        Transforms = [.. this.Transforms.Select(transform => transform.ToStep())],
    };
}

/// <summary>One declarative field transform of the closed set.</summary>
public sealed record TransformDto
{
    /// <summary>The snake_case transform kind: add_field, remove_field, rename_field, or retype_field.</summary>
    public required string Kind { get; init; }

    /// <summary>The primary field name.</summary>
    public required string Field { get; init; }

    /// <summary>The destination field, for a rename.</summary>
    public string? To { get; init; }

    /// <summary>The typed default, for an add.</summary>
    public LiteralDto? Default { get; init; }

    /// <summary>The snake_case coercion, for a retype.</summary>
    public string? Coercion { get; init; }

    /// <summary>Builds the protocol step.</summary>
    /// <returns>The <see cref="FieldTransformStep"/>.</returns>
    /// <exception cref="JsonException">The kind or a member is outside the closed set.</exception>
    public FieldTransformStep ToStep() => new()
    {
        Kind = Kind switch
        {
            "add_field" => FieldTransform.AddField(),
            "remove_field" => FieldTransform.RemoveField(),
            "rename_field" => FieldTransform.RenameField(),
            "retype_field" => FieldTransform.RetypeField(),
            _ => throw new JsonException($"unknown transform kind {this.Kind}"),
        },
        Field = this.Field,
        To = this.To,
        Default = this.Default?.ToLiteral(),
        Coercion = this.Coercion is null ? null : this.Coercion switch
        {
            "integer_to_text" => Protocol.Coercion.IntegerToText(),
            "text_to_integer" => Protocol.Coercion.TextToInteger(),
            "integer_to_number" => Protocol.Coercion.IntegerToNumber(),
            "number_to_integer" => Protocol.Coercion.NumberToInteger(),
            _ => throw new JsonException($"unknown coercion {this.Coercion}"),
        },
    };
}

/// <summary>One typed default literal of the closed set.</summary>
public sealed record LiteralDto
{
    /// <summary>The boolean literal.</summary>
    public bool? Boolean { get; init; }

    /// <summary>The integer literal.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Integer is the documented JSON discriminator name.")]
    public long? Integer { get; init; }

    /// <summary>The text literal.</summary>
    public string? Text { get; init; }

    /// <summary>Builds the protocol literal.</summary>
    /// <returns>The <see cref="TransformLiteral"/>.</returns>
    /// <exception cref="JsonException">No discriminator is set.</exception>
    public TransformLiteral ToLiteral() => this switch
    {
        { Boolean: { } value } => TransformLiteral.Boolean(value),
        { Integer: { } value } => TransformLiteral.Integer(value),
        { Text: { } value } => TransformLiteral.Text(value),
        _ => throw new JsonException("a default literal carries exactly one discriminator"),
    };
}

/// <summary>The dry-run answer: the plan with its evidence and its bounded problem list.</summary>
public sealed record MigrationPlanDto(
    string Manifest,
    string Collection,
    ulong FromVersion,
    ulong ToVersion,
    bool Ready,
    ulong Documents,
    string SourceDigest,
    string ResultDigest,
    TransformCountsDto TransformCounts,
    PlanProblemDto[] Problems,
    bool ProblemsTruncated)
{
    /// <summary>Builds from the protocol plan.</summary>
    /// <param name="plan">The protocol plan.</param>
    /// <returns>The DTO.</returns>
    public static MigrationPlanDto Of(MigrationPlan plan) => new(
        plan.Manifest,
        plan.Collection,
        plan.FromVersion,
        plan.ToVersion,
        plan.Ready,
        plan.Documents,
        plan.SourceDigest,
        plan.ResultDigest,
        TransformCountsDto.Of(plan.TransformCounts),
        [.. plan.Problems.Select(PlanProblemDto.Of)],
        plan.ProblemsTruncated);
}

/// <summary>The apply answer: the recorded receipt.</summary>
public sealed record MigrationReceiptDto(
    string Collection,
    string Manifest,
    ulong FromVersion,
    ulong ToVersion,
    string SourceDigest,
    string ResultDigest,
    ulong Documents,
    ulong Transformed,
    TransformCountsDto TransformCounts,
    ulong Pages,
    string RollbackLimit,
    bool AcknowledgedNotRollbackable,
    bool AlreadyApplied)
{
    /// <summary>Builds from the protocol receipt.</summary>
    /// <param name="receipt">The protocol receipt.</param>
    /// <returns>The DTO.</returns>
    public static MigrationReceiptDto Of(MigrationReceipt receipt) => new(
        receipt.Collection,
        receipt.Manifest,
        receipt.FromVersion,
        receipt.ToVersion,
        receipt.SourceDigest,
        receipt.ResultDigest,
        receipt.Documents,
        receipt.Transformed,
        TransformCountsDto.Of(receipt.TransformCounts),
        receipt.Pages,
        receipt.RollbackLimit.Variant.ToSnakeCase(),
        receipt.AcknowledgedNotRollbackable,
        receipt.AlreadyApplied);
}

/// <summary>The per-kind transform application counts.</summary>
public sealed record TransformCountsDto(ulong Added, ulong Removed, ulong Renamed, ulong Retyped)
{
    /// <summary>Builds from the protocol counts.</summary>
    /// <param name="counts">The protocol counts.</param>
    /// <returns>The DTO.</returns>
    public static TransformCountsDto Of(TransformCounts counts) => new(counts.Added, counts.Removed, counts.Renamed, counts.Retyped);
}

/// <summary>One typed problem of a blocked plan.</summary>
/// <param name="Document">The document the problem was found in.</param>
/// <param name="Field">The field the problem names.</param>
/// <param name="Kind">The snake_case problem kind: transform_rejected or target_violations.</param>
/// <param name="Rejection">The snake_case rejection, for a rejected transform.</param>
/// <param name="Violations">The typed violations, for a target violation.</param>
public sealed record PlanProblemDto(string Document, string Field, string Kind, string? Rejection, ViolationDto[] Violations)
{
    /// <summary>Builds from the protocol problem.</summary>
    /// <param name="problem">The protocol problem.</param>
    /// <returns>The DTO.</returns>
    public static PlanProblemDto Of(PlanProblem problem) => new(
        problem.Document,
        problem.Field,
        problem.Kind.Variant.ToSnakeCase(),
        problem.Rejection?.Variant.ToSnakeCase(),
        [.. (problem.Violations ?? []).Select(violation => new ViolationDto(violation.Field, violation.Kind.Variant.ToSnakeCase()))]);
}
