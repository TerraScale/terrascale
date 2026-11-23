using System.Collections.Concurrent;
using TerraScale.Contracts.Consistency;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed class CausalBookmarks
{
    private const string DatabasesSegment = "/databases/";
    private readonly ConcurrentDictionary<string, CausalBookmark> _bookmarks = new(StringComparer.Ordinal);

    public string? For(Uri? uri) => Key(uri) is { } key && _bookmarks.TryGetValue(key, out var bookmark) ? bookmark.Encode() : null;

    public void Observe(Uri? uri, string? encoded)
    {
        if (Key(uri) is not { } key || encoded is not { Length: > 0 } || !CausalBookmark.TryDecode(encoded, out var observed))
        {
            return;
        }

        _bookmarks.AddOrUpdate(key, observed, (_, current) => current.Merge(observed));
    }

    private static string? Key(Uri? uri)
    {
        if (uri?.AbsolutePath is not { } path || !path.StartsWith("/v1/projects/", StringComparison.Ordinal))
        {
            return null;
        }

        var databases = path.IndexOf(DatabasesSegment, StringComparison.Ordinal);
        if (databases < 0)
        {
            return null;
        }

        var start = databases + DatabasesSegment.Length;
        var end = path.IndexOf('/', start);
        return end < 0 ? path : path[..end];
    }
}
