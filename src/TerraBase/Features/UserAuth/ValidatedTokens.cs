using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace TerraBase.Features.UserAuth;

internal sealed class ValidatedTokens([FromKeyedServices(UserAuthenticationHandler.SchemeName)] TimeProvider? time = null)
{
    private const int Capacity = 4096;

    private readonly ConcurrentDictionary<string, Accepted> _entries = new(StringComparer.Ordinal);

    internal sealed record Accepted(
        IReadOnlyList<SecurityKey> Keys, DateTime ValidTo, IReadOnlyList<(string Type, string Value)> Claims,
        string? Project, string? Database, string? Scope, IReadOnlyList<IPNetwork[]> Allowlists, (string Id, string Generation)? Key);

    public static string KeyOf(string token) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public bool TryGet(string key, IReadOnlyList<SecurityKey> keys, out Accepted accepted)
    {
        if (_entries.TryGetValue(key, out var found) && ReferenceEquals(found.Keys, keys)
            && (time ?? TimeProvider.System).GetUtcNow().UtcDateTime < found.ValidTo)
        {
            accepted = found;
            return true;
        }

        accepted = null!;
        return false;
    }

    public void Add(string key, Accepted accepted)
    {
        if (_entries.Count >= Capacity)
        {
            _entries.Clear();
        }

        _entries[key] = accepted;
    }
}
