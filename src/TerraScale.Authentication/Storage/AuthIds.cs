using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Storage;

public static class AuthIds
{
    private static readonly TerraScaleIdGenerator Generator = new();

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string New() => Generator.NewId();

    public static string Derived(string instanceId, string kind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var framed = string.Create(CultureInfo.InvariantCulture, $"{instanceId.Length}:{instanceId}{kind.Length}:{kind}{key.Length}:{key}");
        var hash = SHA256.HashData(StrictUtf8.GetBytes(framed));
        return new NUlid.Ulid(hash[..16]).ToString();
    }

    public static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return email.Trim().ToLowerInvariant();
    }
}
