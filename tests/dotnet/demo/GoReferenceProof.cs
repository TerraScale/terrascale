using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Dashboard.E2E;

internal static class GoReferenceProof
{
    internal static string CaptureDigest(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relative in new[]
        {
            "tests/dotnet/e2e/GoReferenceCaptureJourneyTests.cs",
            "tests/dotnet/e2e/TerraScale.Dashboard.E2E.csproj",
            "tests/dotnet/go-reference/main.go",
            "tests/dotnet/parity.mjs",
            "tests/parity/snapshot.js"
        }.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relative))));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string RenderingDigest(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in new[] { "internal", "cmd/dashboard", "web", "public" }.Select(directory => Path.Combine(root, directory))
            .Where(Directory.Exists).SelectMany(directory => Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            .Where(path => !path.EndsWith("_test.go", StringComparison.Ordinal) && !path.EndsWith(".br", StringComparison.Ordinal))
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path) + "\0"));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(path)));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
