#!/usr/bin/env dotnet run
#:package Blake3@3.0.2
#:include contracts.lib.cs

using System.Text.Json;
using TerraScale.Ci;

Console.Out.Flush();
Console.Error.Flush();
ExitImmediate(Run().GetAwaiter().GetResult());

async Task<int> Run()
{

// Port of the former scripts/verify_contracts.py — verify the committed
// contract bundle without contacting the registry.


var root = Path.GetFullPath(Path.Join(Path.GetDirectoryName(RepoSourcePath()), ".."));
string pinnedVersion;
try
{
    pinnedVersion = (await File.ReadAllTextAsync(Path.Join(root, "CONTRACT_VERSION"))).Trim();
}
catch (IOException error)
{
    Console.Error.WriteLine($"contracts: committed CONTRACT_VERSION is unavailable: {error.Message}");
    return 1;
}
if (!Contracts.IsVersion(pinnedVersion))
{
    Console.Error.WriteLine($"contracts: committed CONTRACT_VERSION is invalid: '{pinnedVersion}'");
    return 1;
}
try
{
    var bundle = Path.Join(root, "contracts", pinnedVersion);
    var (manifest, _) = Contracts.ValidateBundle(bundle, pinnedVersion);
    using var vectors = Contracts.LoadJson(Path.Join(bundle, "msgpack/message-vectors.json"));
    var manifestSha = Contracts.Sha256Hex(
        File.ReadAllBytes(Path.Join(bundle, "msgpack/terrascale.sync.v1.msgpack.json")));
    string? pinned = null;
    if (vectors.RootElement.ValueKind == JsonValueKind.Object
        && vectors.RootElement.TryGetProperty("manifestSha256", out var pinnedValue)
        && pinnedValue.ValueKind == JsonValueKind.String)
    {
        pinned = pinnedValue.GetString();
    }
    if (pinned != manifestSha)
    {
        Console.Error.WriteLine("contracts: MessagePack corpus pins a different manifest SHA-256");
        return 1;
    }
    var vectorList = vectors.RootElement.TryGetProperty("vectors", out var vectorElement)
        && vectorElement.ValueKind == JsonValueKind.Array ? vectorElement.GetArrayLength() : 0;
    if (vectorList < 11)
    {
        Console.Error.WriteLine("contracts: MessagePack corpus is incomplete");
        return 1;
    }
    if (!File.Exists(Path.Join(bundle, "msgpack/hostile-vectors.json")))
    {
        Console.Error.WriteLine("contracts: hostile MessagePack corpus is missing");
        return 1;
    }
    Console.WriteLine($"contracts: verified committed {manifest.GetProperty("contractVersion").GetString()} bundle");
    return 0;
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
