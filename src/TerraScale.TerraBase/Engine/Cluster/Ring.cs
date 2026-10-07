using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TerraBase.Engine.Cluster;

/// <summary>
/// Consistent hashing over the members of a view (docs/design/anna-core.md §5.2): every member owns
/// <see cref="TokensPerMember"/> tokens, and a key belongs to the members clockwise from its hash. The first is its primary.
/// </summary>
// anna-full (TS-649): per-key replication vectors add memory replicas of hot keys on top of this placement.
internal sealed class Ring
{
    public const int TokensPerMember = 64;

    private readonly (ulong Token, ClusterMember Member)[] _tokens;

    public Ring(ClusterView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _tokens = [.. view.Members
            .SelectMany(member => Enumerable.Range(0, TokensPerMember).Select(index => (Hash($"{member.Name}#{index}"), member)))
            .OrderBy(token => token.Item1).ThenBy(token => token.member.Name, StringComparer.Ordinal)];
    }

    /// <summary>The primary of <paramref name="key"/>, or <see langword="null"/> in an empty view.</summary>
    public ClusterMember? Primary(string key)
    {
        if (_tokens.Length == 0)
        {
            return null;
        }

        var hash = Hash(key);
        var index = Array.BinarySearch(_tokens, (hash, null!), Comparer<(ulong Token, ClusterMember Member)>.Create((a, b) => a.Token.CompareTo(b.Token)));
        index = index >= 0 ? index : ~index;
        return _tokens[index == _tokens.Length ? 0 : index].Member;
    }

    // The first 8 bytes of SHA-256, the same on every member and every platform.
    private static ulong Hash(string value) => BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
