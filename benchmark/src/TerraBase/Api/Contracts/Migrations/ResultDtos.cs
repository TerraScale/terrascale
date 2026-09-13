// <copyright file="ResultDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using TerraBase.Protocol;

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
