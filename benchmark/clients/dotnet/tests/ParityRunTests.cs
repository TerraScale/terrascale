using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiBench;
using TUnit.Core;

namespace BenchClient.Tests;

[SupportedOSPlatform("linux")]
public sealed class ParityRunTests
{
    internal const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    internal const string Peers = "# node region ipv4\nts-gru-1 gru 100.64.0.1\nts-sjc-1 sjc 100.64.0.2\nts-fra-1 fra 100.64.0.3\n";

    internal sealed class Run : IDisposable
    {
        internal DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory("dotnet-parity-");
        internal string Credentials => Path.Combine(Directory.FullName, "credentials.json");
        internal string Output => Path.Combine(Directory.FullName, "parity.jsonl");
        internal StringWriter Error { get; } = new();

        internal static string Valid => $$"""{"api_key":"{{FakeAuth.Key}}","project":"{{Project}}"}""";

        internal Run(string? credentials = null)
        {
            File.WriteAllText(Credentials, credentials ?? Valid);
            File.SetUnixFileMode(Credentials, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        internal Task<int> StartAsync(FakeTerraBase server, ParityRig? rig = null, string peers = Peers, CancellationToken interrupt = default) =>
            ParityRun.RunAsync(Credentials, Output, () => ParityPeers.Parse(peers), rig ?? server.Rig(), Error, interrupt).WaitAsync(TimeSpan.FromSeconds(60));

        internal JsonElement[] Rows() => [.. File.ReadAllLines(Output).Select(line => JsonDocument.Parse(line).RootElement)];

        public void Dispose() => Directory.Delete(recursive: true);
    }

    internal static string Text(JsonElement row, string name) => row.GetProperty(name).GetString()!;

    [Test]
    public async Task FullMatrixPinsSetupToTheFirstRegionAlternatesLanesAndDeletesTheOwnedDatabase()
    {
        using var run = new Run();
        var server = new FakeTerraBase();
        server.OutputAtCreate = () => File.ReadAllText(run.Output);
        var code = await run.StartAsync(server);
        await Assert.That(run.Error.ToString()).IsEqualTo("");
        await Assert.That(code).IsEqualTo(0);
        var rows = run.Rows();
        var database = Text(rows[1], "database");
        await Assert.That(Regex.IsMatch(database, "^api-perf-[0-9a-z]{26}$")).IsTrue();
        await Assert.That(Text(rows[0], "home")).IsEqualTo("fra");
        await Assert.That(server.Owned!.Contains($"\"type\":\"owned\",\"database\":\"{database}\"", StringComparison.Ordinal)).IsTrue();
        // Create, collection, two seeds and the delete all carry the home pin; reads carry each cell's region.
        await Assert.That(server.Setup.ToArray()).IsEquivalentTo(new[] { "POST fra", "POST fra", "PUT fra", "PUT fra", "DELETE fra" });
        var cells = rows.Where(row => Text(row, "type") == "cell").ToArray();
        await Assert.That(cells.Length).IsEqualTo(3 * 3 * 2 * 2 * 3);
        await Assert.That(cells.All(cell => cell.GetProperty("rejection").ValueKind == JsonValueKind.Null)).IsTrue();
        await Assert.That(string.Join(',', cells.Take(3).Select(cell => Text(cell, "lane")))).IsEqualTo("raw-h1,raw-h2,sdk");
        await Assert.That(string.Join(',', cells.Skip(12).Take(3).Select(cell => Text(cell, "lane")))).IsEqualTo("sdk,raw-h2,raw-h1");
        await Assert.That(string.Join(',', cells.Select(cell => Text(cell, "region")).Distinct())).IsEqualTo("fra,gru,sjc");
        await Assert.That(cells.Select(cell => cell.GetProperty("payload_bytes").GetInt32()).Distinct().Order().ToArray()).IsEquivalentTo(new[] { 256, 4096 });
        await Assert.That(cells.All(cell => cell.GetProperty("wire_replays").ValueKind == JsonValueKind.Null && cell.GetProperty("auth_pinned").GetInt64() == 0)).IsTrue();
        var cleanup = rows[^2];
        await Assert.That(Text(cleanup, "deletion")).IsEqualTo("logical_accepted");
        await Assert.That(Text(cleanup, "remote_erasure")).IsEqualTo("unverified");
        await Assert.That(rows[^1].GetProperty("accepted").GetBoolean()).IsTrue();
        await Assert.That(File.GetUnixFileMode(run.Output)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task FirstRejectedCellStopsTheRunAfterItsEvidenceIsWritten()
    {
        using var run = new Run();
        var code = await run.StartAsync(new FakeTerraBase("sjc-region"));
        await Assert.That(run.Error.ToString()).IsEqualTo("api-bench: parity stopped (rejected:region); evidence retained in the output" + Environment.NewLine);
        await Assert.That(code).IsEqualTo(1);
        var cells = run.Rows().Where(row => Text(row, "type") == "cell").ToArray();
        await Assert.That(cells.Length).IsEqualTo(2 * 36 + 1);
        await Assert.That(Text(cells[^1], "region")).IsEqualTo("sjc");
        await Assert.That(Text(cells[^1], "rejection")).IsEqualTo("region");
        await Assert.That(Text(run.Rows()[^2], "deletion")).IsEqualTo("logical_accepted");
    }

    [Test]
    [Arguments("unknown-member")]
    [Arguments("duplicate-member")]
    [Arguments("empty")]
    [Arguments("oversize")]
    [Arguments("fifo")]
    [Arguments("missing-member")]
    [Arguments("bad-key")]
    [Arguments("group-readable")]
    [Arguments("symlink")]
    [Arguments("existing-output")]
    [Arguments("symlink-output")]
    [Arguments("peers")]
    public async Task InputIsRefusedBeforeAnyNetworkUseWithoutEchoingIt(string variant)
    {
        var valid = Run.Valid;
        using var run = new Run(variant switch
        {
            "unknown-member" => valid[..^1] + ""","secret":"do-not-print"}""",
            "duplicate-member" => valid[..^1] + $$""","project":"{{Project}}"}""",
            "empty" => "",
            "oversize" => valid + new string(' ', 4096),
            "missing-member" => $$"""{"api_key":"{{FakeAuth.Key}}"}""",
            "bad-key" => valid.Replace(FakeAuth.Key, "do-not-print", StringComparison.Ordinal),
            _ => valid,
        });
        var elsewhere = Path.Combine(run.Directory.FullName, "elsewhere");
        if (variant == "fifo")
        {
            File.Delete(run.Credentials);
            using var fifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { "-m", "600", run.Credentials } })!;
            await fifo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(fifo.ExitCode).IsEqualTo(0);
        }
        if (variant == "group-readable") File.SetUnixFileMode(run.Credentials, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        if (variant == "symlink")
        {
            File.Move(run.Credentials, elsewhere);
            File.CreateSymbolicLink(run.Credentials, elsewhere);
        }
        if (variant == "existing-output") File.WriteAllText(run.Output, "keep");
        if (variant == "symlink-output") File.CreateSymbolicLink(run.Output, elsewhere);
        var server = new FakeTerraBase();
        var factories = 0;
        var rig = server.Rig() with
        {
            Data = connections => { Interlocked.Increment(ref factories); return server; },
            Auth = () => { Interlocked.Increment(ref factories); return new FakeAuth(3600); },
        };
        var code = await Task.Run(() => run.StartAsync(server, rig, variant == "peers" ? "ts-gru-1 gru 100.64.0.1\nts-sjc-1 sjc 100.64.0.2\n" : Peers))
            .WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(code).IsEqualTo(2);
        await Assert.That(factories + server.Requests).IsEqualTo(0);
        await Assert.That(run.Error.ToString().Contains("do-not-print", StringComparison.Ordinal)).IsFalse();
        await Assert.That(run.Error.ToString().Contains(FakeAuth.Key, StringComparison.Ordinal)).IsFalse();
        if (variant == "existing-output") await Assert.That(File.ReadAllText(run.Output)).IsEqualTo("keep");
        else if (variant == "symlink-output") await Assert.That(File.Exists(elsewhere)).IsFalse();
        else await Assert.That(File.Exists(run.Output)).IsFalse();
    }
}
