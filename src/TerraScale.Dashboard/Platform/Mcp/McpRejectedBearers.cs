using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Dashboard.Platform.Mcp;

public sealed class McpRejectedBearers(TimeProvider time)
{
    private const int Capacity = 10_000;
    private static readonly TimeSpan Remembered = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> rejected = new(StringComparer.Ordinal);

    public bool Contains(string bearer, string project) =>
        rejected.TryGetValue(Key(bearer, project), out var until) && until > time.GetUtcNow();

    public void Remember(string bearer, string project)
    {
        var now = time.GetUtcNow();
        if (rejected.Count >= Capacity)
        {
            foreach (var (key, until) in rejected)
            {
                if (until <= now)
                {
                    rejected.TryRemove(key, out _);
                }
            }

            if (rejected.Count >= Capacity)
            {
                rejected.Clear();
            }
        }

        rejected[Key(bearer, project)] = now + Remembered;
    }

    private static string Key(string bearer, string project) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bearer + "\n" + project)));
}
