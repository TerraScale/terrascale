using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>Query projection: keeps only the listed members, with their parent objects.</summary>
internal static class Projection
{
    public static IReadOnlyList<DocumentPath> Normalize(IEnumerable<DocumentPath> paths)
    {
        var result = new List<DocumentPath>();
        foreach (var path in paths.Where(path => !path.IsId))
        {
            if (result.Any(ancestor => Covers(ancestor, path)))
            {
                continue;
            }

            result.RemoveAll(descendant => Covers(path, descendant));
            result.Add(path);
        }

        return result;
    }

    private static bool Covers(DocumentPath ancestor, DocumentPath path) =>
        ancestor.Segments!.Count <= path.Segments!.Count
        && ancestor.Segments.SequenceEqual(path.Segments.Take(ancestor.Segments.Count), StringComparer.Ordinal);

    public static JsonElement Apply(JsonElement document, IReadOnlyList<DocumentPath> paths, CancellationToken cancellationToken)
    {
        var result = new JsonObject();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = document;
            var found = true;
            foreach (var segment in path.Segments!)
            {
                if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(segment, out source))
                {
                    found = false;
                    break;
                }
            }

            if (!found)
            {
                continue;
            }

            var target = result;
            foreach (var segment in path.Segments!.Take(path.Segments!.Count - 1))
            {
                if (target[segment] is not JsonObject child)
                {
                    child = [];
                    target[segment] = child;
                }

                target = child;
            }

            target[path.Segments![^1]] = JsonNode.Parse(source.GetRawText(), new JsonNodeOptions { PropertyNameCaseInsensitive = false }, RequestJson.StoredDocumentOptions);
        }

        return JsonSerializer.SerializeToElement(result, TerraBaseJsonContext.Default.JsonObject);
    }
}
