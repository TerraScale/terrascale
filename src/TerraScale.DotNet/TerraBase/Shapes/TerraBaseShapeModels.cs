using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Configures a <see cref="TerraBaseShapeClient"/>. Deadlines, limits and retries are fixed by the SDK.</summary>
public sealed record TerraBaseShapeClientOptions
{
    /// <summary>HTTPS origin of the TerraBase client listener, for example <c>https://api.terrascale.tech</c>. Set this or EndpointResolver, exclusively.</summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>Resolves the HTTPS client origin per request, so a subscription can follow failover. Set this or BaseAddress, exclusively.</summary>
    public ITerraBaseEndpointResolver? EndpointResolver { get; init; }

    /// <summary>A TerraBase API key (<c>tb_…</c>) with <c>documents:read</c>. Set this or AccessTokenProvider, exclusively.</summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Supplies the current bearer credential (<c>documents:read</c>) per request. Set this or ApiKey, exclusively.
    /// The provider must be thread-safe and honor cancellation; acquisition shares the request deadline.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"TerraBase shape client options for {BaseAddress}";
}

/// <summary>One document of a shape.</summary>
/// <param name="Version">Opaque Document API version (<c>tbv1-…</c>); it can be passed to conditional writes. Never parse it.</param>
/// <param name="Document">The stored JSON object, as materialized when its response was sealed.</param>
public sealed record TerraBaseShapeDocument(string Version, JsonElement Document);

/// <summary>
/// The local replica of a shape after one response was validated and applied as a whole. Each state is a complete,
/// immutable snapshot; replace the previous one.
/// </summary>
public sealed record TerraBaseShapeState
{
    /// <summary>Every document of the shape by id (ordinal).</summary>
    public required IReadOnlyDictionary<string, TerraBaseShapeDocument> Documents { get; init; }

    /// <summary>The lineage handle (<c>h1-…</c>) that served this state. It changes after a refetch or failover.</summary>
    public required string Handle { get; init; }

    /// <summary>The offset the shape continues from (<c>snap.&lt;S&gt;.&lt;P&gt;</c> while a snapshot is paged, then decimal).</summary>
    public required string Offset { get; init; }

    /// <summary>
    /// The opaque cross-machine resume cursor, returned byte for byte; <see langword="null"/> until every page of the
    /// snapshot is applied.
    /// </summary>
    public string? CursorResume { get; init; }

    /// <summary>Whether this state reached the serving machine's sealed head. It does not promise read-your-writes.</summary>
    public required bool UpToDate { get; init; }
}

internal sealed record ShapeBody(
    string ShapeId, string Handle, string Offset, string NextOffset, string CoveredOffset, string ValuesAt, bool UpToDate, string CursorResume,
    IReadOnlyList<ShapeChange> Changes) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Changes);
}

internal sealed record ShapeChange(string Offset, IReadOnlyList<ShapeOperation> Ops) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Ops);
}

internal sealed record ShapeOperation(string Op, string Id, string? Version = null, JsonElement? Document = null);

internal sealed record ShapeEpochKey(string Handle, string Epoch, string KeyId, string Key);

/// <summary>Source-generated JSON for the shape wire format (snake_case members; trimming and native AOT safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, MaxDepth = TerraBaseJsonContext.MaxResponseDepth,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ShapeBody))]
[JsonSerializable(typeof(ShapeEpochKey))]
internal sealed partial class TerraBaseShapeJsonContext : JsonSerializerContext;
