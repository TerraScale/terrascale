using System.Net;
using System.Text.Json;

namespace BenchClient;

internal static class Cli
{
    internal const string Usage = "bench-client run --workload <file> --region <gru|sfo> --out <results.jsonl>";

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"] or ["-h"])
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }

        try
        {
            if (Dns.GetHostName().Split('.')[0].Equals("mariogk-desktop", StringComparison.OrdinalIgnoreCase))
                throw new PreflightException("HOST_EXCLUDED", "Benchmarks must not run on mariogk-desktop.");

            var options = Parse(args);
            await Workload.ReadAsync(options.Workload);
            // Check SDK lifecycle before credentials, network calls, or opening results.
            throw SdkPrerequisites.BlockedRun();
        }
        catch (PreflightException failure)
        {
            await error.WriteLineAsync($"{failure.Code}: {failure.Message}");
            return failure.Code == "SDK_BLOCKED" ? 1 : 2;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // File paths, parser input and SDK bodies may contain credentials.
            await error.WriteLineAsync("INVALID_WORKLOAD: Cannot read a bounded, valid workload JSON object.");
            return 2;
        }
    }

    internal static CliOptions Parse(string[] args)
    {
        if (args.Length != 7 || args[0] != "run")
            throw new PreflightException("INVALID_ARGUMENTS", Usage);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (args[i] is not ("--workload" or "--region" or "--out") ||
                string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(args[i], args[i + 1]))
                throw new PreflightException("INVALID_ARGUMENTS", Usage);
        }

        if (values["--region"] is not ("gru" or "sfo"))
            throw new PreflightException("INVALID_ARGUMENTS", "--region must be gru or sfo.");

        return new(values["--workload"], values["--region"], values["--out"]);
    }
}

internal sealed record CliOptions(string Workload, string Region, string Out);
internal sealed class PreflightException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
