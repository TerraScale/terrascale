using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TerraScale.TerraBase.Engine;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>Document as returned by the client API.</summary>
/// <param name="Id">Document id.</param>
/// <param name="Version">Version, incremented on every write.</param>
/// <param name="Data">The stored JSON object.</param>
public sealed record DocumentBody(string Id, long Version, JsonElement Data)
{
    internal static DocumentBody From(StoredDocument document)
    {
        using var json = JsonDocument.Parse(document.Json);
        return new DocumentBody(document.Id, document.Version, json.RootElement.Clone());
    }
}

/// <summary>Route parsing and limits shared by the document endpoints.</summary>
internal static class DocumentRoute
{
    /// <summary>Base route of one collection.</summary>
    public const string Collection = "/v1/databases/{database}/collections/{collection}/documents";

    /// <summary>Largest accepted document body.</summary>
    public const int MaxDocumentBytes = 1024 * 1024;

    private const int MaxIdLength = 256;

    public static DatabaseName Database(HttpContext context) =>
        DatabaseName.TryParse(context.Request.RouteValues["database"] as string, out var name)
            ? name
            : throw new BadHttpRequestException("database must be a lowercase name of at most 63 characters");

    public static string CollectionName(HttpContext context) =>
        DatabaseName.TryParse(context.Request.RouteValues["collection"] as string, out var name)
            ? name.Value
            : throw new BadHttpRequestException("collection must be a lowercase name of at most 63 characters");

    public static DocumentKey Key(HttpContext context)
    {
        var id = context.Request.RouteValues["id"] as string;
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength || id.Any(char.IsControl))
        {
            throw new BadHttpRequestException("id must be 1 to 256 characters without control characters");
        }

        return new DocumentKey(Database(context), CollectionName(context), id);
    }

    /// <summary>Reads a bounded body that must be one JSON object, and returns its UTF-8 bytes.</summary>
    public static async ValueTask<ReadOnlyMemory<byte>> ReadObjectAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxDocumentBytes;
        }

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(buffer.ToArray().AsMemory());
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new BadHttpRequestException("the document must be a JSON object");
            }
        }
        catch (JsonException)
        {
            throw new BadHttpRequestException("the document must be valid JSON");
        }

        return buffer.ToArray();
    }
}
