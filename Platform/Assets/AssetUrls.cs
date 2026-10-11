using System.Security.Cryptography;

namespace TerraScale.Dashboard.Platform.Assets;

public sealed class AssetUrls(IWebHostEnvironment environment)
{
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
        using var stream = file.CreateReadStream();
        return Convert.ToHexStringLower(SHA256.HashData(stream).AsSpan(0, 6));
    }
}
