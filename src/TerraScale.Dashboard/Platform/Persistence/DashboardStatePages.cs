using System.Runtime.CompilerServices;
using System.Text.Json;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence
{
    // Query on the producer, projecting ownership only when erasing potentially large records.
    public static class DashboardStatePages
    {
        public const int PageSize = 32;
        public static async IAsyncEnumerable<TerraBaseDocument> ReadAsync(IDashboardState state, string collection,
            IReadOnlyDictionary<string, string> scope, IReadOnlyList<string>? fields, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (scope.Any(pair => !TerraScaleIds.IsValid(pair.Value)))
            {
                throw new IOException("Cleanup scope is invalid.");
            }

            var filter = scope.Count == 0 ? (JsonElement?)null : JsonSerializer.SerializeToElement(new
            { and = scope.Select(pair => new { eq = new[] { "/" + pair.Key, pair.Value } }).ToArray() });
            string? cursor = null;
            do
            {
                var page = await state.QueryPageAsync(collection, new() { Filter = filter, Project = fields, Limit = PageSize, Cursor = cursor }, cancellationToken);
                if (page.Items.Count > PageSize || page.NextCursor is not null && page.NextCursor == cursor)
                {
                    throw new IOException("Cleanup query page is invalid.");
                }

                foreach (var document in page.Items)
                {
                    if (document.State != "value" || scope.Any(pair => !document.Document.TryGetProperty(pair.Key, out var value) || value.GetString() != pair.Value))
                    {
                        throw new IOException("Cleanup query ownership is invalid.");
                    }

                    yield return document;
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
    }
}
