#!/usr/bin/env dotnet run
#:package Blake3@3.0.2
#:include contracts.lib.cs

using System.Security.Cryptography;
using System.Text.Json;
using TerraScale.Ci;

Console.Out.Flush();
Console.Error.Flush();
ExitImmediate(Run().GetAwaiter().GetResult());

async Task<int> Run()
{

// Port of the former inline python heredoc in .forgejo/workflows/ci.yml —
// download the immutable producer archive, SHA-256 it against the pin, and
// byte-compare the producer bundle with the committed bundle.


try
{
    var url = Environment.GetEnvironmentVariable("CONTRACT_URL") ?? "";
    if (url.Length == 0)
    {
        Contracts.Die("CONTRACT_URL is required in CI");
    }
    if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != "https" || parsed.Host.Length == 0)
    {
        Contracts.Die("CONTRACT_URL must be an absolute HTTPS URL");
    }
    var root = Path.GetFullPath(Path.Join(Path.GetDirectoryName(RepoSourcePath()), ".."));
    var version = (await File.ReadAllTextAsync(Path.Join(root, "CONTRACT_VERSION"))).Trim();
    var archiveSha256 = string.Concat(
        (await File.ReadAllTextAsync(Path.Join(root, "CONTRACT_ARCHIVE_SHA256"))).Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
    if (archiveSha256.Length != 64 || archiveSha256.Any(c => !char.IsAsciiHexDigit(c)))
    {
        Contracts.Die("CONTRACT_ARCHIVE_SHA256 must be a 64-character hexadecimal digest");
    }

    var temporary = Path.Combine(Path.GetTempPath(), $"terrascale-contract-ci-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporary);
    try
    {
        var payload = Contracts.DownloadArchive(url);
        var actual = Convert.ToHexStringLower(SHA256.HashData(payload));
        if (!string.Equals(actual, archiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            Contracts.Die($"archive SHA-256 {actual} does not match CONTRACT_ARCHIVE_SHA256");
        }
        var producer = Contracts.UnpackArchive(payload, temporary);
        var (producerManifest, producerDigests) = Contracts.ValidateBundle(producer, version);
        var committed = Path.Join(root, "contracts", version);
        var (committedManifest, committedDigests) = Contracts.ValidateBundle(committed, version);
        if (!JsonElement.DeepEquals(producerManifest, committedManifest) || !producerDigests.SequenceEqual(committedDigests))
        {
            Contracts.Die("downloaded producer bundle differs from the committed bundle");
        }
        string[] RegularFiles(string bundleRoot) =>
            Directory.EnumerateFiles(bundleRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(bundleRoot, path))
                .ToArray();
        var producerFiles = RegularFiles(producer).Order().ToArray();
        var committedFiles = RegularFiles(committed).Order().ToArray();
        if (!producerFiles.SequenceEqual(committedFiles))
        {
            Contracts.Die("downloaded producer file set differs from the committed bundle");
        }
        foreach (var relative in producerFiles)
        {
            if (!File.ReadAllBytes(Path.Join(producer, relative)).SequenceEqual(
                    File.ReadAllBytes(Path.Join(committed, relative))))
            {
                Contracts.Die($"downloaded producer artifact differs: {relative}");
            }
        }
        Console.WriteLine($"contracts: verified immutable {version} producer bundle ({archiveSha256})");
        return 0;
    }
    finally
    {
        Directory.Delete(temporary, recursive: true);
    }
}
catch (ContractError error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static string RepoSourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

}

// dotnet's SIGCHLD reaper FailFasts (ECHILD) at container teardown after child
// processes were reaped by WaitForExit; skip managed shutdown entirely.
[System.Runtime.InteropServices.DllImport("libc", EntryPoint = "_exit")]
static extern void ExitImmediate(int code);
