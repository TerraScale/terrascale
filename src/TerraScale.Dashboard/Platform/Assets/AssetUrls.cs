using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace TerraScale.Dashboard.Platform.Assets;

public sealed class AssetUrls(IWebHostEnvironment environment)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Modified, long Length, string Hash)> versions = new(StringComparer.Ordinal);
    public const string DatastarIntegrity = "sha384-twbOvafhSBQ0rJDNPzJdUw6DRvthVgAVfSIG1BQVmCiNO7Qh2zeVn7T5qstgjJ9q";
    public const string DatastarMapIntegrity = "sha384-pt2bLYQEhQctXw5tJaokvLV96hxW/GSwz2K8L+3xltbjrLNwyWS8PG/2NprOqeIS";

    public string Url(string path)
    {
        path = "/" + path.TrimStart('/');
        var version = Version(path);
        return version.Length == 0 ? path : $"{path}?v={version}";
    }

    public string Version(string path)
    {
        var file = environment.WebRootFileProvider.GetFileInfo(path.TrimStart('/'));
        if (!file.Exists || file.IsDirectory) return "";
        // Assets are immutable in a release. Metadata also invalidates versions during development/HMR.
        if (versions.TryGetValue(path, out var saved) && saved.Modified == file.LastModified && saved.Length == file.Length)
            return saved.Hash;
        using var stream = file.CreateReadStream();
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream).AsSpan(0, 6));
        versions[path] = (file.LastModified, file.Length, hash);
        return hash;
    }
}
