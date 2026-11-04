using System.Text.Json;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public static class DashboardStateUpdates
    {
        public static async Task<T> UpdateAsync<T>(this IDashboardState state, string table, string key, Func<T> create, Func<StateRecord, T> read,
            Func<T, T?> change, CancellationToken cancellationToken) where T : class
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var record = await state.GetAsync(table, key, cancellationToken);
                var current = record is null ? create() : read(record);
                if (change(current) is not { } next)
                {
                    return current;
                }

                try
                {
                    await state.PutAsync(table, key, JsonSerializer.SerializeToElement(next),
                        record is null ? StatePrecondition.Absent : StatePrecondition.Matches(record.Version), cancellationToken);
                    return next;
                }
                catch (StateConflictException) when (attempt < 7) { }
            }

            throw new IOException("Dashboard state changed concurrently.");
        }
    }
}
