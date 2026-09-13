// <copyright file="WriteDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text.Json;
using TerraBase.Protocol;

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
public sealed record CommitReceiptDto(
    ulong Sequence,
    ulong Version,
    string OperationId,
    string? Acknowledgement = null,
    string? Backup = null)
{
    /// <summary>Builds from the protocol receipt.</summary>
    /// <param name="receipt">The protocol receipt.</param>
    /// <returns>The DTO.</returns>
    public static CommitReceiptDto Of(CommitReceipt receipt) => new(
        receipt.Sequence,
        receipt.Version,
        receipt.OperationId,
        receipt.Acknowledgement?.Variant.ToSnakeCase(),
        receipt.Backup?.Variant.ToSnakeCase());
}
