// <copyright file="DocumentDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
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
