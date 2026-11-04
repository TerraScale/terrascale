using System.Runtime.CompilerServices;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public static class DashboardStatePages
    {
        public const int PageSize = 32;

        public static async IAsyncEnumerable<StateRecord> ReadAsync(IDashboardState state, string table, IReadOnlyDictionary<string, string> scope,
            IReadOnlyList<string>? fields, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (scope.Any(pair => !TerraScaleIds.IsValid(pair.Value)))
            {
                throw new IOException("Cleanup scope is invalid.");
            }

            string? cursor = null;
            do
            {
                var page = await state.PageAsync(table, scope, fields, PageSize, cursor, cancellationToken);
                if (page.Items.Count > PageSize || page.NextCursor is not null && page.NextCursor == cursor)
                {
                    throw new IOException("Cleanup query page is invalid.");
                }

                foreach (var record in page.Items)
                {
                    if (scope.Any(pair => !record.Body.TryGetProperty(pair.Key, out var value) || value.GetString() != pair.Value))
                    {
                        throw new IOException("Cleanup query ownership is invalid.");
                    }

                    yield return record;
                }

                cursor = page.NextCursor;
            } while (cursor is not null);
        }
    }
}
