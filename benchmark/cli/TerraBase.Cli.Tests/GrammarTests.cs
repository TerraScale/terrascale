// <copyright file="GrammarTests.cs" company="TerraScale">
// TerraBaseV2 (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli.Tests;

/// <summary>
/// The command-grammar and configuration rows (no engine): the
/// documented defaults, the environment overrides, the
/// flag-over-environment precedence, and the fail-closed usage rows —
/// an unknown option, an unknown command, or a missing operand is a
/// usage error with a bounded message, never a guessed invocation.
/// </summary>
public sealed class GrammarTests
{
    private static readonly IReadOnlyDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    /// <summary>The documented defaults resolve from an empty environment.</summary>
    [Test]
    public async Task DefaultsResolveFromAnEmptyEnvironment()
    {
        var (config, error) = Cli.ResolveConfiguration(["status"], NoEnvironment);
        await Assert.That(error).IsNull();
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Mode).IsEqualTo("host");
        await Assert.That(config.Url).IsEqualTo("http://127.0.0.1:8420");
        await Assert.That(config.SocketPath).IsEqualTo("/run/terrabase/engine.tbep.sock");
        await Assert.That(config.Principal).IsEqualTo("terrabase-cli");
        await Assert.That(config.Tenant).IsEqualTo("local");
        await Assert.That(config.OutputFormat).IsEqualTo("human");
    }

    /// <summary>The documented environment variables override the defaults.</summary>
    [Test]
    public async Task EnvironmentOverridesTheDefaults()
    {
        var environment = new Dictionary<string, string?>
        {
            ["TERRABASE_MODE"] = "tbep",
            ["TERRABASE_HOST_URL"] = "http://127.0.0.1:9999",
            ["TERRABASE_TBEP_SOCKET"] = "/tmp/other.sock",
            ["TERRABASE_PRINCIPAL"] = "backend-operator",
            ["TERRABASE_TENANT"] = "tenant-a",
            ["TERRABASE_OUTPUT"] = "json",
        };
        var (config, error) = Cli.ResolveConfiguration(["status"], environment);
        await Assert.That(error).IsNull();
        await Assert.That(config!.Mode).IsEqualTo("tbep");
        await Assert.That(config.Url).IsEqualTo("http://127.0.0.1:9999");
        await Assert.That(config.SocketPath).IsEqualTo("/tmp/other.sock");
        await Assert.That(config.Principal).IsEqualTo("backend-operator");
        await Assert.That(config.Tenant).IsEqualTo("tenant-a");
        await Assert.That(config.OutputFormat).IsEqualTo("json");
    }

    /// <summary>A flag beats the environment variable it names.</summary>
    [Test]
    public async Task FlagsTakePrecedenceOverTheEnvironment()
    {
        var environment = new Dictionary<string, string?> { ["TERRABASE_HOST_URL"] = "http://127.0.0.1:9999", ["TERRABASE_OUTPUT"] = "json" };
        var (config, error) = Cli.ResolveConfiguration(["status", "--url", "http://127.0.0.1:8421", "--output", "human"], environment);
        await Assert.That(error).IsNull();
        await Assert.That(config!.Url).IsEqualTo("http://127.0.0.1:8421");
        await Assert.That(config.OutputFormat).IsEqualTo("human");
    }

    /// <summary>The <c>--json</c> shorthand selects machine output.</summary>
    [Test]
    public async Task TheJsonShorthandSelectsMachineOutput()
    {
        var (config, error) = Cli.ResolveConfiguration(["--json", "status"], NoEnvironment);
        await Assert.That(error).IsNull();
        await Assert.That(config!.OutputFormat).IsEqualTo("json");
    }

    /// <summary>An unknown option is a usage error, not a guess.</summary>
    [Test]
    public async Task AnUnknownOptionIsAUsageError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await Cli.RunAsync(["--nope", "status"], stdout, stderr, environment: NoEnvironment);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("usage");
        await Assert.That(stdout.ToString()).IsEmpty();
    }

    /// <summary>An unknown command is a usage error.</summary>
    [Test]
    public async Task AnUnknownCommandIsAUsageError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await Cli.RunAsync(["frobnicate"], stdout, stderr, environment: NoEnvironment);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("usage");
    }

    /// <summary>A missing operand is a usage error.</summary>
    [Test]
    public async Task AMissingOperandIsAUsageError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await Cli.RunAsync(["document", "read", "orders"], stdout, stderr, environment: NoEnvironment);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("usage");
    }

    /// <summary>An invalid mode fails closed.</summary>
    [Test]
    public async Task AnInvalidModeFailsClosed()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await Cli.RunAsync(["--mode", "carrier-pigeon", "status"], stdout, stderr, environment: NoEnvironment);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("mode");
    }

    /// <summary>A malformed operand or option value is a bounded usage error, never a crash.</summary>
    /// <param name="args">The malformed invocation.</param>
    /// <param name="word">The word the bounded refusal must name.</param>
    /// <returns>A task that completes when the refusal held.</returns>
    private static async Task RefusedAsync(string[] args, string word)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await Cli.RunAsync(args, stdout, stderr, environment: NoEnvironment);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(stderr.ToString()).Contains(word);
        // The refusal is bounded: one diagnostic line and the grammar
        // pointer, never a stack trace, never a core dump.
        var lines = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0].StartsWith("terrabase: ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(stderr.ToString()).DoesNotContain("Exception");
    }

    /// <summary>A non-JSON <c>document write</c> body is a usage refusal, not a parse crash.</summary>
    [Test]
    public async Task AMalformedDocumentBodyIsAUsageError() =>
        await RefusedAsync(["document", "write", "orders", "d-01", "not-json"], "JSON");

    /// <summary>A non-numeric <c>--max-items</c> is a usage refusal, not a parse crash.</summary>
    [Test]
    public async Task AMalformedMaxItemsIsAUsageError() =>
        await RefusedAsync(["document", "browse", "orders", "--max-items", "many"], "max-items");

    /// <summary>A non-numeric query <c>--max-items</c> is a usage refusal, not a parse crash.</summary>
    [Test]
    public async Task AMalformedQueryBoundIsAUsageError() =>
        await RefusedAsync(["query", "from orders", "--max-items", "x"], "max-items");

    /// <summary>A non-numeric <c>--idle</c> is a usage refusal, not a parse crash.</summary>
    [Test]
    public async Task AMalformedIdleIsAUsageError() =>
        await RefusedAsync(["changes", "--idle", "abc"], "idle");

    /// <summary>A negative <c>--idle</c> is refused before the subscription opens, not after it.</summary>
    [Test]
    public async Task ANegativeIdleIsAUsageError() =>
        await RefusedAsync(["changes", "--idle", "-5"], "idle");

    /// <summary>A non-numeric <c>--max-events</c> is a usage refusal, not a silent unbounded tail.</summary>
    [Test]
    public async Task AMalformedMaxEventsIsAUsageError() =>
        await RefusedAsync(["changes", "--max-events", "soon"], "max-events");

    /// <summary>A non-numeric audit <c>--limit</c> is a usage refusal, not a parse crash.</summary>
    [Test]
    public async Task AMalformedAuditLimitIsAUsageError() =>
        await RefusedAsync(["audit", "--limit", "none"], "limit");

    /// <summary>A malformed <c>--expected</c> is a usage refusal in host mode too, not a typed error later.</summary>
    [Test]
    public async Task AMalformedExpectedIsAUsageErrorInHostMode() =>
        await RefusedAsync(["document", "write", "orders", "d-01", "{}", "--expected", "later"], "expected");

    /// <summary>Every machine envelope carries the stable header and exactly one payload property.</summary>
    [Test]
    public async Task MachineEnvelopesCarryTheStableHeaderAndOnePayload()
    {
        // A representative result envelope (help answers one machine line).
        var helpOut = new StringWriter();
        var helpErr = new StringWriter();
        var helpExit = await Cli.RunAsync(["--json", "help"], helpOut, helpErr, environment: NoEnvironment);
        await Assert.That(helpExit).IsEqualTo(0);
        await Assert.That(helpErr.ToString()).IsEmpty();
        var result = MachineEnvelope.Parse(helpOut.ToString()).Single();
        await MachineEnvelope.AssertWellFormedAsync(result, "help", "host");
        await Assert.That(result.TryGetProperty("result", out _)).IsTrue();

        // A representative error envelope (an endpoint that cannot be
        // reached answers the unavailable envelope on standard error).
        var statusOut = new StringWriter();
        var statusErr = new StringWriter();
        var statusExit = await Cli.RunAsync(
            ["--json", "--url", "http://127.0.0.1:9", "status"], statusOut, statusErr, environment: NoEnvironment);
        await Assert.That(statusExit).IsEqualTo(3);
        await Assert.That(statusOut.ToString()).IsEmpty();
        var error = MachineEnvelope.Parse(statusErr.ToString()).Single();
        await MachineEnvelope.AssertWellFormedAsync(error, "status", "host");
        await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unavailable");
    }

    /// <summary>A typed error is one bounded human line in human mode, never a raw JSON envelope.</summary>
    [Test]
    public async Task TypedErrorsAreBoundedHumanLinesInHumanMode()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await Cli.RunAsync(
            ["--url", "http://127.0.0.1:9", "status"], stdout, stderr, environment: NoEnvironment);
        await Assert.That(exit).IsEqualTo(3);
        await Assert.That(stdout.ToString()).IsEmpty();
        var text = stderr.ToString();
        await Assert.That(text).Contains("terrabase:");
        await Assert.That(text).Contains("status");
        await Assert.That(text).DoesNotContain("cliVersion");
        await Assert.That(text).DoesNotContain("{");
    }

    /// <summary><c>help</c> prints the whole grammar and exits zero.</summary>
    [Test]
    public async Task HelpPrintsTheWholeGrammar()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await Cli.RunAsync(["help"], stdout, stderr, environment: NoEnvironment);
        await Assert.That(exit).IsEqualTo(0);
        var text = stdout.ToString();
        await Assert.That(text).Contains("changes --follow");
        await Assert.That(text).Contains("document write");
        await Assert.That(text).Contains("--mode");
        await Assert.That(text).Contains("--socket");
        await Assert.That(text).Contains("sessions");
        await Assert.That(stderr.ToString()).IsEmpty();
    }
}
