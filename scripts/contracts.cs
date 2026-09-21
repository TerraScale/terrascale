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
        return null;
    }

    string Token() => Guid.NewGuid().ToString("N")[..16];

    var version = Flag("--version") ?? Environment.GetEnvironmentVariable("CONTRACT_VERSION") ?? "v1";
    var source = Flag("--source") ?? Environment.GetEnvironmentVariable("CONTRACT_SOURCE") ?? "";
    var url = Flag("--url") ?? Environment.GetEnvironmentVariable("CONTRACT_URL") ?? "";
    var archiveSha256 = Flag("--archive-sha256") ?? Environment.GetEnvironmentVariable("CONTRACT_ARCHIVE_SHA256") ?? "";

    try
    {
        if (!Contracts.IsVersion(version))
        {
            Contracts.Die($"invalid contract version '{version}'");
        }
        var repo = RepoRoot(RepoSourcePath());
        var pinPath = Path.Join(repo, "CONTRACT_VERSION");
        Contracts.RegularFile(pinPath, "CONTRACT_VERSION");
        var pinned = File.ReadAllText(pinPath).Trim();
        if (pinned != version)
        {
            Contracts.Die($"CONTRACT_VERSION contains '{pinned}', requested '{version}'");
        }
        var contractsRoot = Path.Join(repo, "contracts");
        if (Contracts.IsSymlink(contractsRoot))
        {
            Contracts.Die($"refusing to use symlink contracts directory {contractsRoot}");
        }
        Directory.CreateDirectory(contractsRoot);
        var destination = Path.Join(contractsRoot, version);
        var generatedTarget = Path.Join(repo, "src/generated_contract.rs");
        if ((File.Exists(destination) || Directory.Exists(destination))
            && (!Directory.Exists(destination) || Contracts.IsSymlink(destination)))
        {
            Contracts.Die($"contract destination is unsafe: {destination}");
        }
        if (File.Exists(generatedTarget) && Contracts.IsSymlink(generatedTarget))
        {
            Contracts.Die($"refusing to replace symlink generated artifact {generatedTarget}");
        }
        var temporary = Path.Combine(Path.GetTempPath(), $"terrascale-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            var producer = Contracts.LoadProducer(source, url, archiveSha256, temporary);
            Contracts.ValidateBundle(producer, version);
            var staging = Path.Join(contractsRoot, $".{version}.staging-{Token()}");
            var generatedStaging = Path.Join(repo, $".generated_contract-{Token()}.rs");
            string? backup = null;
            string? generatedBackup = null;
            var installed = false;
            try
            {
                CopyTree(producer, staging);
                Contracts.RejectLinks(staging);
                var (manifest, digests) = Contracts.ValidateBundle(staging, version);
                var corpus = Contracts.ValidateCorpus(staging);
                File.WriteAllText(generatedStaging, Contracts.RenderGenerated(staging, manifest, digests, corpus));
                Contracts.Rustfmt(generatedStaging);
                if (Directory.Exists(destination))
                {
                    backup = Path.Join(contractsRoot, $".{version}.backup-{Token()}");
                    Directory.Move(destination, backup);
                }
                if (File.Exists(generatedTarget))
                {
                    generatedBackup = Path.Join(repo, $".generated_contract.rs.backup-{Token()}");
                    File.Move(generatedTarget, generatedBackup);
                }
                Directory.Move(staging, destination);
                installed = true;
                File.Move(generatedStaging, generatedTarget);
                if (backup is not null)
                {
                    Contracts.ScopedRemove(backup, contractsRoot, $".{version}.backup-");
                }
                if (generatedBackup is not null)
                {
                    File.Delete(generatedBackup);
                }
                Console.WriteLine($"contracts: pinned {version} from producer bundle {producer}");
            }
            catch
            {
                if (File.Exists(generatedStaging))
                {
                    File.Delete(generatedStaging);
                }
                if (Directory.Exists(staging))
                {
                    Contracts.ScopedRemove(staging, contractsRoot, $".{version}.staging-");
                }
                if (installed)
                {
                    var rollback = Path.Join(contractsRoot, $".{version}.rollback-{Token()}");
                    Directory.Move(destination, rollback);
                    if (backup is not null && Directory.Exists(backup))
                    {
                        Directory.Move(backup, destination);
                    }
                    else
                    {
                        Contracts.ScopedRemove(rollback, contractsRoot, $".{version}.rollback-");
                    }
                }
                else if (backup is not null && Directory.Exists(backup) && !Directory.Exists(destination))
                {
                    Directory.Move(backup, destination);
                }
                if (generatedBackup is not null && File.Exists(generatedBackup))
                {
                    if (File.Exists(generatedTarget))
                    {
                        File.Delete(generatedTarget);
                    }
                    File.Move(generatedBackup, generatedTarget);
                }
                throw;
            }
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
        return 0;
    }
    catch (ContractError error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

static void CopyTree(string from, string to)
{
    foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(Path.Join(to, Path.GetRelativePath(from, directory)));
    }
    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        var destination = Path.Join(to, Path.GetRelativePath(from, file));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: true);
    }
}

static string RepoSourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
static string RepoRoot(string sourcePath) => Path.GetFullPath(Path.Join(Path.GetDirectoryName(sourcePath), ".."));

// dotnet's SIGCHLD reaper FailFasts (ECHILD) at container teardown after child
// processes were reaped by WaitForExit; skip managed shutdown entirely.
[System.Runtime.InteropServices.DllImport("libc", EntryPoint = "_exit")]
static extern void ExitImmediate(int code);
