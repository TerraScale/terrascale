using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Declared Draft 2020-12 JSON Schema, separate from inferred fields and uniqueness declarations.</summary>
/// <param name="Schema">Attached schema, or null when detached.</param>
/// <param name="Dialect">Supported dialect when attached; absent when detached.</param>
public sealed record TerraBaseDeclaredSchema(JsonElement? Schema, string? Dialect = null);

public sealed partial class TerraBaseDatabase
{
    /// <summary>Reads the declared JSON Schema; requires documents:read. Uses /collections/{collection}/schema.</summary>
    public async Task<TerraBaseDeclaredSchema> GetDeclaredSchemaAsync(string collection, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, DeclaredSchemaPath(collection), null, Json.TerraBaseDeclaredSchema, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Attaches a self-contained Draft 2020-12 JSON Schema, at most 64 KiB. Requires documents:write.
    /// Applies to future local writes; existing documents and replicated commits are not revalidated. Sent once.
    /// </summary>
    public async Task<TerraBaseDeclaredSchema> PutDeclaredSchemaAsync(string collection, JsonElement schema, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Put, DeclaredSchemaPath(collection), TerraBaseConnection.Json(schema, Json.JsonElement),
            Json.TerraBaseDeclaredSchema, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Detaches the declared schema without deleting documents. Requires documents:write. Sent once.</summary>
    public async Task<TerraBaseDeclaredSchema> DeleteDeclaredSchemaAsync(string collection, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Delete, DeclaredSchemaPath(collection), null, Json.TerraBaseDeclaredSchema, null, cancellationToken).ConfigureAwait(false)).Value;

    private string DeclaredSchemaPath(string collection) => CollectionPath(collection) + "/schema";
}
