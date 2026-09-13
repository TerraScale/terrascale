// <copyright file="MigrationDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text.Json;
using TerraBase.Protocol;

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
