using System.Text.Json;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public static class DashboardStateUpdates
    {
        // Read-modify-write of one document under TerraBase optimistic concurrency. change returns the document to write, or null to write nothing.
        public static async Task<T> UpdateAsync<T>(this IDashboardState state, string collection, string key, Func<T> create, Func<TerraBaseDocument, T> read,
            Func<T, T?> change, CancellationToken cancellationToken) where T : class
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var document = await state.GetAsync(collection, key, cancellationToken);
                var current = document is null ? create() : read(document);
                if (change(current) is not { } next)
                {
                    return current;
                }

                try
                {
                    await state.PutAsync(collection, key, JsonSerializer.SerializeToElement(next),
                        document is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(document.Version), cancellationToken);
                    return next;
                }
                catch (TerraBasePreconditionFailedException) when (attempt < 7) { }
            }
            throw new IOException("Dashboard state changed concurrently.");
        }
    }
}
