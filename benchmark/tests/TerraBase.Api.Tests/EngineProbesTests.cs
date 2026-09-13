// <copyright file="EngineProbesTests.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

/// <summary>
/// The engine-communication probes of the AC (red-then-green): the host
/// talks to the engine only through the SE-14 C# TBEP client — no C
/// ABI, no raw sockets, no storage access. The detector is proven
/// capable of going red (each banned construct, embedded as a fixture,
/// is flagged), then the real host sources are asserted green. The
/// TerraBase.Protocol project is the sanctioned socket path and is out
/// of the probe's scope by design.
/// </summary>
public sealed class EngineProbesTests
{
    /// <summary>The banned constructs of the probe, with their class.</summary>
    public static readonly (string Token, string Class)[] Banned =
    [
        ("[DllImport", "c-abi"),
        ("[LibraryImport", "c-abi"),
        ("NativeMethods", "c-abi"),
        ("new Socket(", "raw-socket"),
        ("UnixDomainSocketEndPoint", "raw-socket"),
        ("TcpClient", "raw-socket"),
        ("TcpListener", "raw-socket"),
        ("UdpClient", "raw-socket"),
        ("new NetworkStream(", "raw-socket"),
        ("File.Open(", "storage"),
        ("File.Create(", "storage"),
        ("File.Delete(", "storage"),
        ("File.Move(", "storage"),
        ("File.WriteAllText", "storage"),
        ("File.AppendAll", "storage"),
        ("new FileStream(", "storage"),
        ("Directory.Delete", "storage"),
        ("Directory.Create", "storage"),
        ("MemoryMappedFile", "storage"),
    ];

    /// <summary>RED: the probe flags every banned construct (the detector works).</summary>
    [Test]
    [MethodDataSource(nameof(ViolatingSnippets))]
    public async Task TheProbeFlagsEachBannedConstruct(ViolatingSnippet snippet)
    {
        var violations = Probe.Scan([("probe.cs", snippet.Source)]);
        await Assert.That(violations).IsNotEmpty();
        await Assert.That(violations.Single().Class).IsEqualTo(snippet.ExpectedClass);
    }

    /// <summary>GREEN: the host sources carry none of the banned constructs.</summary>
    [Test]
    public async Task TheHostTalksToTheEngineOnlyThroughTheTbepClient()
    {
        var sources = Probe.HostSources();
        await Assert.That(sources).IsNotEmpty();
        var violations = Probe.Scan(sources);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>The generated corpus of violating snippets: one per banned construct.</summary>
    public static IEnumerable<ViolatingSnippet> ViolatingSnippets() =>
        Banned.Select((banned, index) => new ViolatingSnippet(
            $"snippet-{index}",
            $$"""
            namespace Probe;
            public sealed class Violation{{index}}
            {
                public void Offend() => _ = "{{banned.Token}}";
            }
            """,
            banned.Class));

    /// <summary>One violating source snippet and the class its flag must carry.</summary>
    /// <param name="Name">The snippet name.</param>
    /// <param name="Source">The source text.</param>
    /// <param name="ExpectedClass">The expected violation class.</param>
    public sealed record ViolatingSnippet(string Name, string Source, string ExpectedClass);
}

/// <summary>The probe itself: scans the host's own C# sources for banned constructs.</summary>
public static class Probe
{
    /// <summary>Every C# source of the API host project (the Protocol project is the sanctioned client).</summary>
    /// <returns>The file names with their full text.</returns>
    public static IEnumerable<(string Name, string Text)> HostSources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "TerraBase.Api")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("the worktree root (src/TerraBase.Api) is not reachable from the test output");
        }

        return Directory
            .EnumerateFiles(Path.Combine(directory.FullName, "src", "TerraBase.Api"), "*.cs", SearchOption.AllDirectories)
            .OrderBy(path => path)
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)));
    }

    /// <summary>Scans sources for the banned constructs.</summary>
    /// <param name="sources">The sources.</param>
    /// <returns>The violations found.</returns>
    public static IReadOnlyList<(string File, string Token, string Class)> Scan(IEnumerable<(string Name, string Text)> sources)
    {
        var violations = new List<(string, string, string)>();
        foreach (var (name, text) in sources)
        {
            foreach (var (token, @class) in EngineProbesTests.Banned)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                {
                    violations.Add((name, token, @class));
                }
            }
        }

        return violations;
    }
}
