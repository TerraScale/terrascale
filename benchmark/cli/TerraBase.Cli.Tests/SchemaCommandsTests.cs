// <copyright file="SchemaCommandsTests.cs" company="TerraScale">
// TerraBaseV2 (SM-04). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli.Tests;

using System.Text.Json;

/// <summary>
/// The SM-04 CLI rows: the four schema-surface commands against a live API
/// host. The host is the real ASP.NET Core surface over a scripted TBEP
/// engine (the engine-backed host is the recorded integrator action, so the
/// fixture scripts the same typed wire answers), and the CLI runs as the
/// real entry point — the same path an operator's terminal takes.
/// </summary>
public sealed class SchemaCommandsTests
{
    private static string SocketPath() =>
        Path.Combine(Path.GetTempPath(), $"tbep-schema-cli-{Guid.NewGuid():N}.sock");

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string url, string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exit = await Cli.RunAsync(
            ["--mode", "host", "--url", url, "--principal", "cli-operator", "--tenant", "tenant-a", "--json", .. args],
            stdout,
            stderr,
            bound.Token).ConfigureAwait(false);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static JsonElement Machine(string text)
    {
        using var document = JsonDocument.Parse(text.Trim());
        return document.RootElement.GetProperty("result").Clone();
    }

    /// <summary>The schema status command reports the recorded version and the migration state.</summary>
    [Test]
    public async Task SchemaStatusReportsTheRecordedState()
    {
        await using var fixture = await SchemaHostFixture.StartAsync();
        await fixture.DeclareAsync("tenant-a", "users", SchemaHostFixture.Schema, 1).ConfigureAwait(false);
        var (exit, stdout, stderr) = await RunAsync(fixture.Url, ["schema", "status", "users"]).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stderr).IsEmpty();
        var result = Machine(stdout);
        await Assert.That(result.GetProperty("collection").GetString()).IsEqualTo("users");
        await Assert.That(result.GetProperty("enforcedVersion").GetUInt64()).IsEqualTo(1ul);
        await Assert.That(result.GetProperty("ruleCount").GetInt32()).IsEqualTo(3);
        await Assert.That(result.GetProperty("migration").GetString()).IsEqualTo("none");
    }

    /// <summary>The validate command names the violated field and exits non-zero for an invalid document.</summary>
    [Test]
    public async Task SchemaValidateNamesTheViolatedField()
    {
        await using var fixture = await SchemaHostFixture.StartAsync();
        await fixture.DeclareAsync("tenant-a", "users", SchemaHostFixture.Schema, 1).ConfigureAwait(false);
        var (exit, stdout, stderr) = await RunAsync(fixture.Url, ["schema", "validate", "users", """{"email":7,"nick":"ada"}"""]).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr).IsEmpty();
        var result = Machine(stdout);
        await Assert.That(result.GetProperty("valid").GetBoolean()).IsFalse();
        await Assert.That(result.GetProperty("violations")[0].GetProperty("field").GetString()).IsEqualTo("email");
        // The verdict is content-free: the document's own values never ride along.
        await Assert.That(stdout.Contains("ada")).IsFalse();
    }

    /// <summary>The dry-run command emits one stable, machine-readable plan line.</summary>
    [Test]
    public async Task MigrationDryRunEmitsAStableMachinePlan()
    {
        await using var fixture = await SchemaHostFixture.StartAsync();
        await fixture.DeclareAsync("tenant-a", "users", SchemaHostFixture.Schema, 1).ConfigureAwait(false);
        var contract = SchemaHostFixture.Contract;
        var first = await RunAsync(fixture.Url, ["migration", "dry-run", "users", contract]).ConfigureAwait(false);
        await Assert.That(first.Exit).IsEqualTo(0);
        await Assert.That(first.Stderr).IsEmpty();

        // The machine shape is stable: the same command answers byte-for-byte.
        var second = await RunAsync(fixture.Url, ["migration", "dry-run", "users", contract]).ConfigureAwait(false);
        await Assert.That(second.Stdout).IsEqualTo(first.Stdout);

        var result = Machine(first.Stdout);
        await Assert.That(result.GetProperty("collection").GetString()).IsEqualTo("users");
        await Assert.That(result.GetProperty("fromVersion").GetUInt64()).IsEqualTo(1ul);
        await Assert.That(result.GetProperty("toVersion").GetUInt64()).IsEqualTo(2ul);
        await Assert.That(result.GetProperty("ready").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("documents").GetUInt64()).IsEqualTo(1ul);
        await Assert.That(result.GetProperty("sourceDigest").GetString()).Length().EqualTo(64);
        await Assert.That(result.GetProperty("resultDigest").GetString()).Length().EqualTo(64);
        await Assert.That(result.GetProperty("problemsTruncated").GetBoolean()).IsFalse();
    }

    /// <summary>The apply command seats the new version and reports the recorded receipt.</summary>
    [Test]
    public async Task MigrationApplySeatsTheNewVersion()
    {
        await using var fixture = await SchemaHostFixture.StartAsync();
        var (exit, stdout, stderr) = await RunAsync(
            fixture.Url,
            ["migration", "apply", "users", SchemaHostFixture.Contract, "--acknowledge-not-rollbackable"]).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stderr).IsEmpty();
        var result = Machine(stdout);
        await Assert.That(result.GetProperty("toVersion").GetUInt64()).IsEqualTo(2ul);
        await Assert.That(result.GetProperty("rollbackLimit").GetString()).IsEqualTo("not_reversible");
        await Assert.That(result.GetProperty("acknowledgedNotRollbackable").GetBoolean()).IsTrue();
    }

    /// <summary>The unknown-flag discipline holds on the new commands: nothing runs.</summary>
    [Test]
    public async Task AnUnknownOptionOnASchemaCommandIsAUsageError()
    {
        await using var fixture = await SchemaHostFixture.StartAsync();
        var (exit, _, stderr) = await RunAsync(fixture.Url, ["schema", "status", "users", "--nope", "1"]).ConfigureAwait(false);
        await Assert.That(exit).IsGreaterThan(0);
        await Assert.That(stderr).Contains("unknown option");
    }
}
