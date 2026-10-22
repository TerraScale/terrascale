using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Presigned sealed shape objects; verify size/hash and decrypt before applying any object.</summary>
/// <param name="Handle">Current shape handle.</param>
/// <param name="Objects">At most 8 objects; empty when the offset is not mirrored or is up to date.</param>
public sealed record TerraBaseShapeManifest(string Handle, IReadOnlyList<TerraBaseShapeMirrorObject> Objects) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Objects);
}

/// <summary>One immutable TBS1 object. The URL is a temporary capability; do not log it.</summary>
/// <param name="Offset">Starting immutable offset.</param>
/// <param name="NextOffset">Continuation offset.</param>
/// <param name="Binding">Authenticated binding.</param>
/// <param name="Object">Object identifier.</param>
/// <param name="Url">Presigned URL, expires at the next epoch.</param>
/// <param name="ExpiresAt">Expiration time.</param>
/// <param name="Epoch">Decimal epoch.</param>
/// <param name="KeyId">Data key ULID.</param>
/// <param name="Sha256">Expected sealed-object SHA-256.</param>
/// <param name="Size">Expected sealed-object bytes.</param>
public sealed record TerraBaseShapeMirrorObject(string Offset,
    [property: JsonPropertyName("next_offset")] string NextOffset, string Binding, string Object, string Url,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt, string Epoch,
    [property: JsonPropertyName("key_id")] string KeyId, string Sha256, long Size)
{
    /// <summary>Omits the signed URL from diagnostics.</summary>
    public override string ToString() => "TerraBase sealed shape object (URL redacted)";
}

public sealed partial class TerraBaseShapeClient
{
    /// <summary>Reads a private no-store manifest for a retained immutable offset. Requires documents:read; sends once, fetches no objects.</summary>
    public Task<TerraBaseShapeManifest> GetManifestAsync(string projectId, string databaseName, string collection,
        string handle, string offset, string? where = null, CancellationToken cancellationToken = default)
    {
        var path = ShapePath(projectId, databaseName, collection) + "/manifest";
        if (!TerraBaseShapeProtocol.IsValidHandle(handle)) throw new ArgumentException("Handle must be h1- and 16 lowercase hexadecimal digits.", nameof(handle));
        ArgumentException.ThrowIfNullOrEmpty(offset);
        if (where is not null) TerraBaseShapeProtocol.ValidateWhere(where);
        return connection.ExchangeAsync(path + TerraBaseConnection.Query(("handle", handle), ("offset", offset), ("where", where)), null,
            headers => headers.CacheControl = new CacheControlHeaderValue { NoStore = true }, async opened =>
            {
                var response = opened.Response;
                if (response.StatusCode != HttpStatusCode.OK)
                    throw (int)response.StatusCode >= 400 ? await TerraBaseException.FromResponseAsync(response, opened.Token).ConfigureAwait(false) : Invalid(response.StatusCode, "unexpected status");
                if (response.Headers.CacheControl is not { NoStore: true } || !IsJson(response))
                    throw Invalid(response.StatusCode, "manifest must be no-store JSON");
                var manifest = TerraBaseConnection.ReadResponse(await connection.ReadBodyAsync(response, opened.Token).ConfigureAwait(false),
                    TerraBaseJsonContext.Wire.TerraBaseShapeManifest, response.StatusCode);
                return manifest.Handle == handle && manifest.Objects.Count <= 8
                    ? manifest : throw Invalid(response.StatusCode, "manifest does not match the request");
            }, cancellationToken);
    }
}
