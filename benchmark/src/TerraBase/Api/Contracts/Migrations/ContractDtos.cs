// <copyright file="ContractDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text.Json;
using TerraBase.Protocol;

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
    /// <exception cref="JsonException">The literal does not set exactly one discriminator.</exception>
    public TransformLiteral ToLiteral()
    {
        var discriminatorCount = (this.Boolean.HasValue ? 1 : 0)
            + (this.Integer.HasValue ? 1 : 0)
            + (this.Text is not null ? 1 : 0);
        if (discriminatorCount != 1)
        {
            throw new JsonException("a default literal carries exactly one discriminator");
        }

        return this switch
        {
            { Boolean: { } value } => TransformLiteral.Boolean(value),
            { Integer: { } value } => TransformLiteral.Integer(value),
            { Text: { } value } => TransformLiteral.Text(value),
            _ => throw new JsonException("a default literal carries exactly one discriminator"),
        };
    }
}
