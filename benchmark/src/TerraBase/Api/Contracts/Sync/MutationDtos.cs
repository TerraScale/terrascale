// <copyright file="MutationDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text.Json;
using TerraBase.Protocol;

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
