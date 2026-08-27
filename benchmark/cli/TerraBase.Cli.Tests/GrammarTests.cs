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
