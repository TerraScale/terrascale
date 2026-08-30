using System.Security.Cryptography;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

/// <summary>
/// Migration evidence for the TerraBase-owned corpus manifest at
/// commit <c>a07ea0b010dbd3b65b9bf1f2536e4410ae0f1daa</c>. This verifies
/// the vendored fixture boundary; it does not qualify a public transport.
/// </summary>
public sealed class TerraScaleSyncV2CorpusIntegrityTests
{
    [Test]
    public async Task TheOwnerManifestResolvesElevenUniquePairsAndCategories()
    {
        var corpus = TerraScaleSyncV2CorpusFixture.LoadVerified();

        await Assert.That(corpus.ManifestSha256).IsEqualTo(TerraScaleSyncV2CorpusFixture.OwnerManifestSha256);
        await Assert.That(corpus.ManifestBytes).IsEqualTo(2755L);
        await Assert.That(corpus.Vectors.Count).IsEqualTo(11);
        await Assert.That(corpus.Vectors.Select(vector => (vector.Corpus, vector.FileName)).Distinct().Count()).IsEqualTo(11);
        await Assert.That(corpus.Vectors.Select(vector => vector.Category).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(11);
        await Assert.That(corpus.Vectors.Count(vector => vector.Corpus == "terrascale.sync.v2")).IsEqualTo(9);
        await Assert.That(corpus.Vectors.Count(vector => vector.Corpus == "terrascale.sync.v2-adr0024")).IsEqualTo(2);
        await Assert.That(corpus.Vectors.Select(vector => vector.Category).Order(StringComparer.Ordinal))
            .IsEquivalentTo(TerraScaleSyncV2CorpusFixture.OwnerCategories.Order(StringComparer.Ordinal));
    }

    [Test]
    public async Task ManifestResolutionRejectsTraversalAndRootedPaths()
    {
        var root = TerraScaleSyncV2CorpusFixture.LoadVerified().RootDirectory;
        (string Corpus, string FileName)[] invalidEntries =
        [
            ("..", "authorization-reset.json"),
            ("../terrascale.sync.v2", "authorization-reset.json"),
            ("terrascale.sync.v2", "../authorization-reset.json"),
            ("terrascale.sync.v2", "nested/authorization-reset.json"),
            (Path.DirectorySeparatorChar == '\\' ? @"C:\\outside" : "/outside", "authorization-reset.json"),
            ("terrascale.sync.v2", Path.DirectorySeparatorChar == '\\' ? @"C:\\outside.json" : "/outside.json"),
            (@"..\\outside", "authorization-reset.json"),
            ("terrascale.sync.v2", @"..\\outside.json"),
        ];

        foreach (var (corpus, fileName) in invalidEntries)
        {
            var failure = Assert.Throws<InvalidDataException>(() =>
                TerraScaleSyncV2CorpusFixture.ResolveVectorPath(root, corpus, fileName));
            await Assert.That(failure.Message).Contains("safe relative path component");
        }
    }
}

internal sealed record TerraScaleSyncV2CorpusVector(
    string Corpus,
    string Category,
    string FileName,
    string FullPath,
    long ExpectedBytes,
    string ExpectedSha256)
{
    internal JsonElement Open()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(FullPath));
        return document.RootElement.Clone();
    }
}

internal sealed record TerraScaleSyncV2CorpusSnapshot(
    string RootDirectory,
    string ManifestPath,
    long ManifestBytes,
    string ManifestSha256,
    IReadOnlyList<TerraScaleSyncV2CorpusVector> Vectors);

internal static class TerraScaleSyncV2CorpusFixture
{
    internal const string OwnerCommit = "a07ea0b010dbd3b65b9bf1f2536e4410ae0f1daa";
    internal const string OwnerManifestSha256 = "9df06df5a2afad153375880a34ec548e3deba6e0a130d12cd645d58488d180d3";
    internal const string ManifestSchema = "terrascale.sync.v2/corpus-manifest@2";

    internal static readonly string[] OwnerCategories =
    [
        "authorization-reset",
        "checkpoint-resume",
        "fresh-session",
        "malformed-frame-rejection",
        "reconnect-replay",
        "region-route-change",
        "repeated-change-delta",
        "resync-required",
        "slow-consumer",
        "slow-consumer-overload",
        "trigger-notification",
    ];

    private static readonly Lazy<TerraScaleSyncV2CorpusSnapshot> Verified =
        new(LoadVerifiedCore, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static TerraScaleSyncV2CorpusSnapshot LoadVerified() => Verified.Value;

    internal static string ResolveVectorPath(string rootDirectory, string corpus, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        RequireSafeComponent(corpus, "corpus");
        RequireSafeComponent(fileName, "file");
        Require(fileName.EndsWith(".json", StringComparison.Ordinal), "the manifest file must end in .json");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var resolved = Path.GetFullPath(Path.Combine(root, corpus, fileName));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root + Path.DirectorySeparatorChar;
        Require(resolved.StartsWith(prefix, comparison),
            $"the manifest pair ({corpus}, {fileName}) resolves outside the corpus root");
        return resolved;
    }

    private static TerraScaleSyncV2CorpusSnapshot LoadVerifiedCore()
    {
        var (rootDirectory, manifestPath) = LocateManifest();
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        Require(manifestBytes.LongLength == 2755L, "the owner manifest byte length is not 2755");
        Require(manifestSha256 == OwnerManifestSha256, "the owner manifest SHA256 does not match the pinned TerraBase commit");

        using var manifest = JsonDocument.Parse(manifestBytes);
        var root = manifest.RootElement;
        Require(RequiredString(root, "schema") == ManifestSchema, "the corpus manifest schema is not @2");
        Require(RequiredString(root, "protocol") == "terrascale.sync.v2", "the corpus manifest protocol is not terrascale.sync.v2");
        Require(RequiredInt64(root, "protocol_version") == 2L, "the corpus manifest protocol version is not 2");
        Require(root.TryGetProperty("vectors", out var vectorEntries) && vectorEntries.ValueKind == JsonValueKind.Array,
            "the corpus manifest vectors property is not an array");

        var vectors = new List<TerraScaleSyncV2CorpusVector>();
        var pairs = new HashSet<(string Corpus, string FileName)>();
        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in vectorEntries.EnumerateArray())
        {
            var names = entry.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
            Require(names.SequenceEqual(["bytes", "category", "corpus", "file", "sha256"], StringComparer.Ordinal),
                "a corpus manifest entry does not have the exact @2 fields");

            var corpus = RequiredString(entry, "corpus");
            var category = RequiredString(entry, "category");
            var fileName = RequiredString(entry, "file");
            var expectedBytes = RequiredInt64(entry, "bytes");
            var expectedSha256 = RequiredString(entry, "sha256");
            Require(expectedBytes >= 0L, $"the manifest byte length for {corpus}/{fileName} is negative");
            Require(expectedSha256.Length == 64 && expectedSha256.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f'),
                $"the manifest SHA256 for {corpus}/{fileName} is not lowercase hexadecimal");
            Require(pairs.Add((corpus, fileName)), $"the manifest repeats the pair ({corpus}, {fileName})");
            Require(categories.Add(category), $"the manifest repeats the category {category}");

            var fullPath = ResolveVectorPath(rootDirectory, corpus, fileName);
            Require(File.Exists(fullPath), $"the manifest vector {corpus}/{fileName} is missing");
            var bytes = File.ReadAllBytes(fullPath);
            Require(bytes.LongLength == expectedBytes,
                $"the manifest byte length for {corpus}/{fileName} does not match the file");
            Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == expectedSha256,
                $"the manifest SHA256 for {corpus}/{fileName} does not match the file");

            using var vector = JsonDocument.Parse(bytes);
            var vectorRoot = vector.RootElement;
            Require(RequiredString(vectorRoot, "schema") == "terrascale.sync.v2/vector-corpus@2",
                $"the vector schema for {corpus}/{fileName} is not @2");
            Require(RequiredString(vectorRoot, "protocol") == "terrascale.sync.v2",
                $"the vector protocol for {corpus}/{fileName} is not terrascale.sync.v2");
            Require(RequiredInt64(vectorRoot, "protocol_version") == 2L,
                $"the vector protocol version for {corpus}/{fileName} is not 2");
            Require(RequiredString(vectorRoot, "category") == category,
                $"the vector category for {corpus}/{fileName} does not match the manifest");

            vectors.Add(new TerraScaleSyncV2CorpusVector(
                corpus,
                category,
                fileName,
                fullPath,
                expectedBytes,
                expectedSha256));
        }

        Require(vectors.Count == 11, "the owner manifest does not contain exactly 11 vectors");
        Require(pairs.Count == 11, "the owner manifest does not contain exactly 11 unique pairs");
        Require(categories.Count == 11, "the owner manifest does not contain exactly 11 unique categories");
        Require(categories.Order(StringComparer.Ordinal).SequenceEqual(OwnerCategories.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "the owner manifest category set does not match the pinned TerraBase commit");
        Require(vectors.Select(vector => vector.Corpus).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .SequenceEqual(["terrascale.sync.v2", "terrascale.sync.v2-adr0024"], StringComparer.Ordinal),
            "the owner manifest corpus set is not exact");

        foreach (var corpus in vectors.GroupBy(vector => vector.Corpus, StringComparer.Ordinal))
        {
            var directory = Path.Combine(rootDirectory, corpus.Key);
            Require(Directory.Exists(directory), $"the manifest corpus directory {corpus.Key} is missing");
            var expectedFiles = corpus.Select(vector => vector.FileName).Order(StringComparer.Ordinal).ToArray();
            var filesOnDisk = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Require(filesOnDisk.SequenceEqual(expectedFiles, StringComparer.Ordinal),
                $"the on-disk vector files for {corpus.Key} do not exactly match its manifest entries");
            Require(!Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any(),
                $"the corpus directory {corpus.Key} contains an unexpected subdirectory");
        }

        return new TerraScaleSyncV2CorpusSnapshot(
            rootDirectory,
            manifestPath,
            manifestBytes.LongLength,
            manifestSha256,
            vectors.OrderBy(vector => vector.Corpus, StringComparer.Ordinal)
                .ThenBy(vector => vector.FileName, StringComparer.Ordinal)
                .ToArray());
    }

    private static (string RootDirectory, string ManifestPath) LocateManifest()
    {
        var candidate = AppContext.BaseDirectory;
        for (var depth = 0; depth < 6; depth++)
        {
            var rootDirectory = Path.Combine(candidate, "vectors");
            var canonical = Path.Combine(rootDirectory, "manifest.json");
            if (File.Exists(canonical))
            {
                return (Path.GetFullPath(rootDirectory), Path.GetFullPath(canonical));
            }

            var parent = Path.GetDirectoryName(candidate);
            if (parent is null)
            {
                break;
            }

            candidate = parent;
        }

        throw new InvalidOperationException("the vendored terrascale.sync.v2 corpus manifest was not found");
    }

    private static void RequireSafeComponent(string value, string name)
    {
        Require(!string.IsNullOrWhiteSpace(value)
                && value is not "." and not ".."
                && !Path.IsPathRooted(value)
                && !value.Contains('/')
                && !value.Contains('\\')
                && !value.Contains(':'),
            $"the manifest {name} must be a safe relative path component");
    }

    private static string RequiredString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } result)
        {
            throw new InvalidDataException($"the manifest property {property} is not a string");
        }

        return result;
    }

    private static long RequiredInt64(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out var result))
        {
            throw new InvalidDataException($"the manifest property {property} is not an integer");
        }

        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
