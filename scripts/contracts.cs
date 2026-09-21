#!/usr/bin/env dotnet run
#:package Blake3@3.0.2
#:include contracts.lib.cs

using System.Text.Json;
using TerraScale.Ci;

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

string ExpandUser(string path) =>
    path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path;

var version = Flag("--version") ?? Environment.GetEnvironmentVariable("CONTRACT_VERSION") ?? "v1";
var source = Flag("--source") ?? Environment.GetEnvironmentVariable("CONTRACT_SOURCE") ?? "";
var url = Flag("--url") ?? Environment.GetEnvironmentVariable("CONTRACT_URL") ?? "";

if (!Contracts.IsVersion(version))
{
    Console.Error.WriteLine($"contracts: invalid contract version '{version}'");
    return 1;
}
var repo = Path.GetFullPath(Path.Join(Path.GetDirectoryName(RepoSourcePath()), ".."));
var sourcePath = source.Length > 0 ? Path.GetFullPath(ExpandUser(source)) : null;
if (sourcePath is not null && url.Length > 0)
{
    Console.Error.WriteLine("contracts: CONTRACT_SOURCE and CONTRACT_URL are mutually exclusive");
    return 1;
}
if (sourcePath is not null)
{
    var allowedSource = Path.GetFullPath(Path.Join(Path.GetDirectoryName(repo)!, "terrabase", "contracts"));
    if (sourcePath != allowedSource)
    {
        Console.Error.WriteLine(
            "contracts: CONTRACT_SOURCE is development-only and must point to " +
            $"../terrabase/contracts ({allowedSource})");
        return 1;
    }
}
if (Environment.GetEnvironmentVariable("CI") is not null && sourcePath is not null)
{
    Console.Error.WriteLine(
        "contracts: CI requires CONTRACT_URL for an immutable published package; local CONTRACT_SOURCE is development-only");
    return 1;
}

string Token() => Guid.NewGuid().ToString("N")[..16];

try
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"terrascale-contract-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryRoot);
    try
    {
        string producer;
        if (sourcePath is not null && Directory.Exists(sourcePath))
        {
            producer = sourcePath;
        }
        else if (url.Length > 0)
        {
            producer = Contracts.UnpackUrl(url, temporaryRoot);
        }
        else
        {
            Contracts.Die(
                "no immutable CONTRACT_URL supplied; local ../terrabase/contracts is available only via explicit CONTRACT_SOURCE (E-1 registry publication)");
            throw new ContractError("unreachable");
        }
        Contracts.ValidateBundle(producer, version);
        var contractsParent = Path.Join(repo, "contracts");
        Directory.CreateDirectory(contractsParent);
        var destination = Path.Join(contractsParent, version);
        var generatedTarget = Path.Join(repo, "generated_contract_test.go");
        var contractTarget = Path.Join(repo, "contract.go");
        if (Contracts.IsSymlink(contractsParent))
        {
            Contracts.Die($"refusing to use symlink contracts directory {contractsParent}");
        }
        if (Contracts.IsSymlink(destination))
        {
            Contracts.Die($"refusing to replace symlink destination {destination}");
        }
        if (Contracts.IsSymlink(generatedTarget))
        {
            Contracts.Die($"refusing to replace symlink generated artifact {generatedTarget}");
        }
        if (Contracts.IsSymlink(contractTarget))
        {
            Contracts.Die($"refusing to replace symlink contract artifact {contractTarget}");
        }
        if ((File.Exists(destination) || Directory.Exists(destination)) && !Directory.Exists(destination))
        {
            Contracts.Die($"refusing to replace non-directory destination {destination}");
        }
        if (Directory.Exists(destination))
        {
            Contracts.RejectLinks(destination);
        }
        // Stage beside the exact version directory. Nothing is installed until
        // this copy, corpus check, and a second full digest pass succeed.
        var staging = Path.Join(contractsParent, $".{version}.staging-{Token()}");
        string? backup = null;
        var generatedStaging = Path.Join(contractsParent, $".{version}.generated-{Token()}");
        string? generatedBackup = null;
        var contractStaging = Path.Join(contractsParent, $".{version}.contract-{Token()}");
        string? contractBackup = null;
        var installed = false;
        try
        {
            // Install only files named by the producer manifest. This prevents
            // an old consumer fixture or an unpinned producer extra entering
            // the committed bundle. The manifest describes those artifacts but
            // is not itself listed as an artifact, so install it explicitly
            // for the validation pass and for the committed bundle's provenance.
            var sourceManifest = Path.Join(producer, "manifest.json");
            Contracts.RegularFile(sourceManifest, "producer manifest");
            Directory.CreateDirectory(staging);
            File.Copy(sourceManifest, Path.Join(staging, "manifest.json"));
            using (var producerManifest = Contracts.LoadJson(sourceManifest))
            {
                foreach (var entry in producerManifest.RootElement.GetProperty("artifacts").EnumerateArray())
                {
                    var relative = entry.GetProperty("path").GetString()!;
                    var sourceFile = Path.Join(producer, relative);
                    var destinationFile = Path.Join(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                    Contracts.RegularFile(sourceFile, $"producer artifact {relative}");
                    File.Copy(sourceFile, destinationFile, overwrite: true);
                }
            }
            Contracts.CopyConformance(producer, staging);
            // The corpus is tied to the exact MessagePack manifest, not merely
            // its protocol name. This catches a stale fixture from another v1.
            using (var vectors = Contracts.LoadJson(Path.Join(staging, "msgpack/message-vectors.json")))
            {
                var expectedSha = Contracts.Sha256Hex(
                    File.ReadAllBytes(Path.Join(staging, "msgpack/terrascale.sync.v1.msgpack.json")));
                string? pinned = null;
                if (vectors.RootElement.ValueKind == JsonValueKind.Object
                    && vectors.RootElement.TryGetProperty("manifestSha256", out var pinnedValue)
                    && pinnedValue.ValueKind == JsonValueKind.String)
                {
                    pinned = pinnedValue.GetString();
                }
                if (pinned != expectedSha)
                {
                    Contracts.Die("MessagePack corpus pins a different manifest SHA-256");
                }
            }
            var (manifest, digests) = Contracts.ValidateBundle(staging, version);
            using var openapi = Contracts.LoadJson(Path.Join(staging, "openapi.json"));
            using var msgpack = Contracts.LoadJson(Path.Join(staging, "msgpack/terrascale.sync.v1.msgpack.json"));
            File.WriteAllText(generatedStaging, Contracts.RenderGenerated(staging, manifest, digests));
            File.WriteAllText(contractStaging, Contracts.RenderContractGo(openapi.RootElement, msgpack.RootElement, version, digests));
            if (File.Exists(generatedTarget))
            {
                Contracts.RegularFile(generatedTarget, generatedTarget);
                generatedBackup = Path.Join(contractsParent, $".{version}.generated-backup-{Guid.NewGuid():N}");
                File.Move(generatedTarget, generatedBackup);
            }
            if (File.Exists(contractTarget))
            {
                Contracts.RegularFile(contractTarget, contractTarget);
                contractBackup = Path.Join(contractsParent, $".{version}.contract-backup-{Guid.NewGuid():N}");
                File.Move(contractTarget, contractBackup);
            }
            if (Directory.Exists(destination))
            {
                backup = Path.Join(contractsParent, $".{version}.backup-{Guid.NewGuid():N}");
                Directory.Move(destination, backup);
            }
            Directory.Move(staging, destination);
            installed = true;
            File.Move(generatedStaging, generatedTarget);
            File.Move(contractStaging, contractTarget);
            if (backup is not null)
            {
                Contracts.SafeRemoveTree(backup, contractsParent, $".{version}.backup-");
            }
            if (generatedBackup is not null)
            {
                Contracts.RegularFile(generatedBackup, generatedBackup);
                File.Delete(generatedBackup);
            }
            if (contractBackup is not null)
            {
                Contracts.RegularFile(contractBackup, contractBackup);
                File.Delete(contractBackup);
            }
            Console.WriteLine($"contracts: pinned {version} from {producer}");
        }
        catch
        {
            Contracts.UnlinkIfExists(generatedStaging);
            Contracts.UnlinkIfExists(contractStaging);
            if (Directory.Exists(staging))
            {
                Contracts.SafeRemoveTree(staging, contractsParent, $".{version}.staging-");
            }
            if (installed)
            {
                var rollback = Path.Join(contractsParent, $".{version}.rollback-{Guid.NewGuid():N}");
                Directory.Move(destination, rollback);
                if (backup is not null && Directory.Exists(backup))
                {
                    Directory.Move(backup, destination);
                }
                else
                {
                    Contracts.SafeRemoveTree(rollback, contractsParent, $".{version}.rollback-");
                }
            }
            else if (backup is not null && Directory.Exists(backup) && !Directory.Exists(destination))
            {
                Directory.Move(backup, destination);
            }
            if (generatedBackup is not null && File.Exists(generatedBackup))
            {
                Contracts.UnlinkIfExists(generatedTarget);
                File.Move(generatedBackup, generatedTarget);
            }
            if (contractBackup is not null && File.Exists(contractBackup))
            {
                Contracts.UnlinkIfExists(contractTarget);
                File.Move(contractBackup, contractTarget);
            }
            throw;
        }
    }
    finally
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }
    return 0;
}
catch (ContractError error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static string RepoSourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
