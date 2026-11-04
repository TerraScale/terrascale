using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Demo
{
    public static class TestStateQuery
    {
        public static StatePage Page(IEnumerable<StateRecord> source, IReadOnlyDictionary<string, string> scope, IReadOnlyList<string>? fields, int limit, string? cursor)
        {
            var matches = source.Where(row => scope.All(pair => row.Body.TryGetProperty(pair.Key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == pair.Value))
                .Where(row => cursor is null || StringComparer.Ordinal.Compare(row.Id, cursor) > 0)
                .OrderBy(row => row.Id, StringComparer.Ordinal).Take(limit + 1).ToArray();
            var items = matches.Take(limit).Select(row => fields is null ? row : row with
            {
                Body = JsonSerializer.SerializeToElement(fields.ToDictionary(field => field, field => row.Body.GetProperty(field)))
            }).ToArray();
            return new(items, matches.Length > items.Length ? items[^1].Id : null);
        }
    }
}
