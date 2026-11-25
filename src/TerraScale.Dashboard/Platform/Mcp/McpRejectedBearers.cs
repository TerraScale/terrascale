using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Dashboard.Platform.Mcp;

public sealed class McpRejectedBearers(TimeProvider time)
{
    private const int Capacity = 10_000;
    private static readonly TimeSpan Remembered = TimeSpan.FromMinutes(1);
    private readonly Lock gate = new();
    private readonly Dictionary<string, DateTimeOffset> rejected = new(StringComparer.Ordinal);

    public bool Contains(string bearer, string project)
    {
        var key = Key(bearer, project);
        lock (gate)
        {
            return rejected.TryGetValue(key, out var until) && until > time.GetUtcNow();
        }
    }

    public void Remember(string bearer, string project)
    {
        var key = Key(bearer, project);
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (!rejected.ContainsKey(key) && rejected.Count >= Capacity)
            {
                foreach (var (stale, until) in rejected)
                {
                    if (until <= now)
                    {
                        rejected.Remove(stale);
                    }
                }

                if (rejected.Count >= Capacity)
                {
                    rejected.Clear();
                }
            }

            rejected[key] = now + Remembered;
        }
    }

    private static string Key(string bearer, string project) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bearer + "\n" + project)));
}
