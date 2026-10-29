using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Demo
{
    // Test-only equality/projection/keyset semantics shared by in-memory and HTTP producers.
    public static class TestStateQuery
    {
        public static TerraBaseDocumentPage Page(IEnumerable<TerraBaseDocument> source, TerraBaseQuery query)
        {
            if (query.Sort is not null || query.Count is not null)
            {
                throw new InvalidOperationException("Unsupported test query shape.");
            }

            var matches = source.Where(row => query.Filter is not { } filter || Matches(row.Document, filter))
                .Where(row => query.Cursor is null || StringComparer.Ordinal.Compare(row.Id, query.Cursor) > 0)
                .OrderBy(row => row.Id, StringComparer.Ordinal).Take((query.Limit ?? 100) + 1).ToArray();
            var items = matches.Take(query.Limit ?? 100).Select(row => query.Project is not { } fields ? row : row with
            {
                Document = JsonSerializer.SerializeToElement(fields.ToDictionary(field => field[1..], field => row.Document.GetProperty(field[1..])))
            }).ToArray();
            return new(items, matches.Length > items.Length ? items[^1].Id : null);
        }
        private static bool Matches(JsonElement document, JsonElement filter)
        {
            return filter.TryGetProperty("and", out var and)
            ? and.EnumerateArray().All(term => Matches(document, term))
            : filter.TryGetProperty("eq", out var eq) && document.TryGetProperty(eq[0].GetString()![1..], out var value) && value.GetRawText() == eq[1].GetRawText();
        }
    }
}
