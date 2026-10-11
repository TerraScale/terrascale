using System.Security.Cryptography;
using System.Text;
using TerraBase.Primitives;

namespace TerraBase.Testing;

internal static class ReplicationDocuments
{
    public static string Id(string label) => EntityIds.IsValid(label)
        ? label
        : new Ulid(SHA256.HashData(Encoding.UTF8.GetBytes(label)).AsSpan(0, 16)).ToString();
}
