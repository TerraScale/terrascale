namespace TerraScale.DotNet.Tests;

public sealed class TerraBaseRenameTests
{
    private static readonly string[] LegacyNames =
    [
        string.Concat("TerraBase", "V2"),
        string.Concat("terrabase", "v2"),
        string.Concat("terra-base", "-v2"),
        string.Concat("terra_base", "_v2"),
        string.Concat("TerraBase", " V2"),
        string.Concat("TerraBase", ".V2")
    ];

    [Test]
    public async Task ActiveConsumerSurfacesDoNotReferenceLegacyTerraBaseNames()
    {
        var repositoryRoot = FindRepositoryRoot();
        var files = EnumerateActiveConsumerFiles(repositoryRoot).ToArray();
        var violations = files
            .SelectMany(path => LegacyNames
                .Where(legacyName => File.ReadAllText(path).Contains(legacyName, StringComparison.Ordinal))
                .Select(legacyName => $"{Path.GetRelativePath(repositoryRoot, path)}: {legacyName}"))
            .ToArray();

        await Assert.That(files.Length).IsGreaterThan(0);
        await Assert.That(violations).IsEmpty();
    }

    private static IEnumerable<string> EnumerateActiveConsumerFiles(string repositoryRoot)
    {
        foreach (var fileName in new[] { "GOAL.md", "README.md", "Directory.Build.props", "BannedSymbols.txt", "TerraScale.DotNet.slnx" })
        {
            yield return Path.Combine(repositoryRoot, fileName);
        }

        foreach (var directoryName in new[] { ".forgejo", ".github", "samples", "scripts", "src", "tests" })
        {
            var directory = Path.Combine(repositoryRoot, directoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repositoryRoot, path);
                if (!relativePath.Split(Path.DirectorySeparatorChar).Any(segment =>
                        segment is "bin" or "obj" or "TestResults" or "artifacts"))
                {
                    yield return path;
                }
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TerraScale.DotNet.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the TerraScale.DotNet repository root.");
    }
}
