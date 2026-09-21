#!/usr/bin/env dotnet run
#:package Blake3@3.0.2
#:include contracts.lib.cs

using TerraScale.Ci;

Console.Out.Flush();
Console.Error.Flush();
ExitImmediate(Run());

int Run()
{
    string? Flag(string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == name)
            {
                return args[index + 1];
            }
        }
        if (args.Contains(name))
        {
            throw new ContractError($"contracts: {name} requires a value");
        }
        return null;
    }

    string? Take(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Flag(name);
            if (value is not null)
            {
                return value;
            }
        }
        return null;
    }

    var version = Take("--version") ?? Environment.GetEnvironmentVariable("CONTRACT_VERSION") ?? "";
    var source = Take("--source")
        ?? Environment.GetEnvironmentVariable("CONTRACT_SOURCE")
        ?? Environment.GetEnvironmentVariable("TERRASCALE_CONTRACT_SOURCE") ?? "";
    var url = Take("--url")
        ?? Environment.GetEnvironmentVariable("CONTRACT_URL")
        ?? Environment.GetEnvironmentVariable("TERRASCALE_CONTRACT_URL")
        ?? Environment.GetEnvironmentVariable("CONTRACT_REGISTRY_URL")
        ?? Environment.GetEnvironmentVariable("TERRASCALE_CONTRACT_REGISTRY_URL") ?? "";
    var archiveSha256 = Take("--archive-sha256")
        ?? Environment.GetEnvironmentVariable("CONTRACT_ARCHIVE_SHA256")
        ?? Environment.GetEnvironmentVariable("TERRASCALE_CONTRACT_ARCHIVE_SHA256")
        ?? Environment.GetEnvironmentVariable("CONTRACT_BUNDLE_SHA256")
        ?? Environment.GetEnvironmentVariable("TERRASCALE_CONTRACT_BUNDLE_SHA256") ?? "";
    var corpusSource = Take("--corpus-source")
        ?? Environment.GetEnvironmentVariable("CONTRACT_CORPUS_SOURCE")
        ?? Environment.GetEnvironmentVariable("TERRASCALE_CONTRACT_CORPUS_SOURCE") ?? "";

    try
    {
        Contracts.Install(
            version: version,
            repoRoot: RepoRoot(RepoSourcePath()),
            source: source,
            url: url,
            archiveSha256: archiveSha256,
            corpusSource: corpusSource);
        return 0;
    }
    catch (ContractError error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

static string RepoSourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
static string RepoRoot(string sourcePath) => Path.GetFullPath(Path.Join(Path.GetDirectoryName(sourcePath), ".."));

// dotnet's SIGCHLD reaper FailFasts (ECHILD) at container teardown after child
// processes were reaped by WaitForExit; skip managed shutdown entirely.
[System.Runtime.InteropServices.DllImport("libc", EntryPoint = "_exit")]
static extern void ExitImmediate(int code);
