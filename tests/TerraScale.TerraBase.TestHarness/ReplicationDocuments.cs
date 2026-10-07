using System.Security.Cryptography;
using System.Text;

namespace TerraBase.Testing;

internal static class ReplicationDocuments
{
    public static string Id(string label) => Guid.TryParseExact(label, "D", out _)
        ? label
        : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(label)).AsSpan(0, 16)).ToString("D");
}
