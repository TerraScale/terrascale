using System.Runtime.CompilerServices;

namespace TerraScale.DotNet.TerraBase;

public sealed partial class TerraBaseDatabase
{
    /// <summary>
    /// Streams query pages, preserving counts, groups and explain metadata. Starts at the query's opaque cursor and
    /// follows every non-null continuation, including after empty pages. Fetches only when enumeration advances;
    /// disposing the enumerator stops pagination. Continuations clear Count; the original query is unchanged.
    /// Requests are never prefetched. Repeated or cyclic continuations throw <see cref="InvalidDataException"/>.
    /// </summary>
    public async IAsyncEnumerable<TerraBaseDocumentPage> QueryPagesAsync(string collection, TerraBaseQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var current = query;
        // Brent's cycle detector retains one opaque cursor, independent of page count.
        var anchor = query.Cursor;
        ulong power = 1, length = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await QueryAsync(collection, current, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return page;
            if (page.NextCursor is not { } cursor) yield break;
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(cursor, anchor, StringComparison.Ordinal))
                throw new InvalidDataException("TerraBase returned a cyclic query continuation.");
            if (++length == power)
            {
                anchor = cursor;
                length = 0;
                if (power <= ulong.MaxValue / 2) power *= 2;
            }
            current = current with { Cursor = cursor, Count = false };
        }
    }

    /// <summary>
    /// Streams documents in query order, following opaque continuations even after empty pages. Cancellation is
    /// checked before each buffered document; the next page is fetched only after the current page is consumed.
    /// Dispose the enumerator to stop. Use <see cref="QueryPagesAsync"/> for aggregate groups, counts or explain metadata.
    /// </summary>
    public async IAsyncEnumerable<TerraBaseDocument> QueryDocumentsAsync(string collection, TerraBaseQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var page in QueryPagesAsync(collection, query, cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return document;
            }
        }
    }
}
