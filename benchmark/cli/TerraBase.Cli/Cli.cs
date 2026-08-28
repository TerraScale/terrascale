// <copyright file="Cli.cs" company="TerraScale">
// TerraBaseV2 (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli;

using System.Text;
using System.Text.Json;

/// <summary>
/// The operator CLI of TerraBaseV2 (SE-13): status, document
/// browse/read/write, TQL query execution, the <c>changes --follow</c>
/// tail with checkpoint resume, and session/admin diagnostics — every
/// command a typed operation. The default mode drives the API host's
/// typed HTTP surface (SE-08); the documented direct mode drives the
/// same typed operation set against a local engine process through
/// the SE-14 TBEP client (never a raw socket, never a C ABI). Machine
/// output is a stable, versioned envelope; human output uses the
/// glossary vocabulary exactly; no credentials exist on this local
/// surface and none are logged.
/// </summary>
public static class Cli
{
    /// <summary>The version of the machine-readable output envelope; additive changes bump it.</summary>
    public const int MachineOutputVersion = 1;

    /// <summary>The exit code of a completed command.</summary>
    public const int ExitOk = 0;

    /// <summary>The exit code of a usage error: the grammar or configuration was refused, nothing ran.</summary>
    public const int ExitUsage = 1;

    /// <summary>The exit code of a typed refusal: the host or engine answered its bounded error.</summary>
    public const int ExitRefused = 2;

    /// <summary>The exit code of an unavailable endpoint: the surface could not be reached or is not serving.</summary>
    public const int ExitUnavailable = 3;

    private static readonly string[] UsageLines =
    [
        "usage: terrabase [options] <command> [operands] [command options]",
        string.Empty,
        "options (flag over environment variable over default):",
        "  --mode host|tbep        host (default): the API host's typed HTTP surface",
        "                          tbep: the same typed operation set on a local engine process",
        "  --url URL               the host's loopback listener (default http://127.0.0.1:8420)",
        "  --socket PATH           the engine's UDS socket in tbep mode (default /run/terrabase/engine.tbep.sock)",
        "  --principal NAME        the backend-supplied principal (default terrabase-cli)",
        "  --tenant NAME           the backend-supplied tenant (default local)",
        "  --output human|json     the output convention; --json is the machine shorthand",
        string.Empty,
        "commands:",
        "  status                       the engine and topology summary",
        "  document read COLLECTION DOCUMENT",
        "  document write COLLECTION DOCUMENT BODY [--idempotency-key KEY] [--expected any|VERSION]",
        "  document delete COLLECTION DOCUMENT [--idempotency-key KEY] [--expected any|VERSION]",
        "  document browse COLLECTION [--max-items N]",
        "  query TQL-TEXT [--max-items N]      query cancel QUERY-ID",
        "  changes [--follow] [--from SEQUENCE] [--collections A,B] [--idle MS] [--max-events N]",
        "          changes --follow tails the Change Event Backbone and reconnects from its checkpoint",
        "  sessions                     audit [--limit N]            metrics",
        "  engine health|status|topology|shutdown [--reason operator|supervisor]",
        "  schema status [COLLECTION]",
        "  schema validate COLLECTION DOCUMENT_PATH",
        "  migration dry-run COLLECTION",
        "  migration apply COLLECTION [--idempotency-key KEY] [--acknowledge-not-rollbackable]",
        "  help",
        string.Empty,
        "environment: TERRABASE_MODE, TERRABASE_HOST_URL, TERRABASE_TBEP_SOCKET,",
        "TERRABASE_PRINCIPAL, TERRABASE_TENANT, TERRABASE_OUTPUT",
        string.Empty,
        "exit codes: 0 completed, 1 usage, 2 typed refusal, 3 unavailable",
    ];

    /// <summary>Resolves the configuration of one invocation: flag over environment over default.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="environment">The environment variables.</param>
    /// <returns>The configuration, or the bounded usage error that refused it.</returns>
    public static (CliConfiguration? Configuration, string? Error) ResolveConfiguration(string[] args, IReadOnlyDictionary<string, string?> environment)
    {
        Dictionary<string, string>? flags = null;
        try
        {
            flags = CliGrammar.ParseFlags(args);
        }
        catch (SurfaceUsageException failure)
        {
            return (null, failure.Message);
        }

        string? Flag(string name) => flags!.TryGetValue(name, out var value) ? value : null;
        string Pick(string flag, string variable, string fallback) =>
            Flag(flag) ?? NonEmpty(environment, variable) ?? fallback;

        var mode = Pick("mode", "TERRABASE_MODE", "host");
        if (mode is not ("host" or "tbep"))
        {
            return (null, $"usage: --mode is host or tbep, not \"{mode}\"");
        }

        var output = Flag("json") is not null ? "json" : Pick("output", "TERRABASE_OUTPUT", "human");
        if (output is not ("human" or "json"))
        {
            return (null, $"usage: --output is human or json, not \"{output}\"");
        }

        var url = Pick("url", "TERRABASE_HOST_URL", CliConfiguration.DefaultUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            return (null, $"usage: --url is an http(s) URL, not \"{url}\"");
        }

        return (new CliConfiguration(
            Mode: mode,
            Url: parsed.ToString().TrimEnd('/'),
            SocketPath: Pick("socket", "TERRABASE_TBEP_SOCKET", CliConfiguration.DefaultSocketPath),
            Principal: Pick("principal", "TERRABASE_PRINCIPAL", CliConfiguration.DefaultPrincipal),
            Tenant: Pick("tenant", "TERRABASE_TENANT", CliConfiguration.DefaultTenant),
            OutputFormat: output), null);
    }

    /// <summary>Runs one CLI invocation and answers its exit code.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="stdout">The standard output stream.</param>
    /// <param name="stderr">The standard error stream.</param>
    /// <param name="cancellation">The operator's interrupt (the follow tail stops gracefully on it).</param>
    /// <param name="environment">The environment variables, or <c>null</c> for the process environment.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellation = default,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var variables = environment ?? ProcessEnvironment();
        var (configuration, configurationError) = ResolveConfiguration(args, variables);
        if (configurationError is not null || configuration is null)
        {
            return RefuseUsage(configurationError ?? "usage: the arguments did not resolve", stderr, configuration, args);
        }

        CliGrammar.Command command;
        try
        {
            command = CliGrammar.ParseCommand(args);
        }
        catch (SurfaceUsageException failure)
        {
            return RefuseUsage(failure.Message, stderr, configuration, args);
        }

        var output = new OutputWriter(configuration, stdout, stderr);
        if (command.Name == "help")
        {
            output.Help([.. UsageLines]);
            await stdout.FlushAsync(cancellation).ConfigureAwait(false);
            return ExitOk;
        }

        try
        {
            await using ITerraBaseSurface surface = configuration.Mode == "tbep"
                ? await DirectTbepSurface.ConnectAsync(configuration, cancellation).ConfigureAwait(false)
                : new HostSurface(configuration);
            var exit = await Commands.DispatchAsync(command, surface, output, cancellation).ConfigureAwait(false);
            // The flushes run uncancelled: an operator interrupt that
            // stopped a tail must not discard its already-written lines.
            await stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await stderr.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return exit;
        }
        catch (SurfaceUsageException failure)
        {
            return RefuseUsage(failure.Message, stderr, configuration, args);
        }
        catch (SurfaceRefusalException failure)
        {
            output.Error(command.DisplayName, failure.Code, failure.Detail);
            await stderr.FlushAsync(cancellation).ConfigureAwait(false);
            return ExitRefused;
        }
        catch (SurfaceUnavailableException failure)
        {
            output.Error(command.DisplayName, "unavailable", failure.Message);
            await stderr.FlushAsync(cancellation).ConfigureAwait(false);
            return ExitUnavailable;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The operator interrupted a running command (the follow
            // tail's usual stop): a graceful, bounded exit.
            await stderr.FlushAsync(cancellation).ConfigureAwait(false);
            return ExitOk;
        }
    }

    private static int RefuseUsage(string message, TextWriter stderr, CliConfiguration? configuration, string[] args)
    {
        var command = ExtractCommandName(args);
        var machine = configuration?.OutputFormat == "json" || args.Contains("--json", StringComparer.Ordinal);
        if (machine)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("cliVersion", MachineOutputVersion);
                writer.WriteString("command", command);
                writer.WriteString("mode", configuration?.Mode ?? "host");
                writer.WriteStartObject("error");
                writer.WriteString("code", "usage");
                writer.WriteString("detail", message);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            stderr.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        }
        else
        {
            stderr.WriteLine($"terrabase: {message}");
            stderr.WriteLine("usage: terrabase help prints the grammar");
        }

        stderr.Flush();
        return ExitUsage;
    }

    private static string ExtractCommandName(string[] args)
    {
        // Best effort for the usage envelope's command label: the same
        // walk the grammar uses, an option's value never counted. The
        // label itself comes from the grammar's table — the longest
        // matching command prefix — so a multi-word command path is
        // always named in full, never by its first word alone.
        var plain = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                plain.Add(token);
                continue;
            }

            var body = token[2..];
            if (body.Contains('='))
            {
                continue;
            }

            var next = index + 1 < args.Length ? args[index + 1] : null;
            if (next is not null && !next.StartsWith("--", StringComparison.Ordinal) && !BooleanFlag(body))
            {
                index++;
            }
        }

        return CliGrammar.CommandLabel(plain);

        static bool BooleanFlag(string name) => name is "json" or "follow";
    }

    private static string? NonEmpty(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static IReadOnlyDictionary<string, string?> ProcessEnvironment()
    {
        var dictionary = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            dictionary[(string)entry.Key] = (string?)entry.Value;
        }

        return dictionary;
    }
}
