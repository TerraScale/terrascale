using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraScale.Auth.Hosting;
using TerraScale.Logging;

namespace TerraScale.Auth.Fast.Platform;

// Console.SetOut is process-wide, so the console tests never overlap.
[NotInParallel(nameof(LoggingTests))]
public sealed class LoggingTests
{
    private const string ExceptionSecret = "exception-secret-4d1e7a";

    [Test]
    public async Task Console_records_are_json_and_withhold_exception_messages()
    {
        var console = new StringWriter();
        var stdout = Console.Out;
#pragma warning disable TUnit0055 // The writer is restored in finally, and NotInParallel keeps other console tests out.
        Console.SetOut(console);
#pragma warning restore TUnit0055
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.AddTerraScaleLogging("ts-auth");
            builder.Services.AddSafeLogging();
            using (var host = builder.Build())
            {
#pragma warning disable CA1848, CA2254 // The point is to log the way arbitrary library code does.
                host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TerraScale.Auth.Tests")
                    .LogError(new InvalidOperationException(ExceptionSecret), "Token exchange failed for {ClientId}", "client-1");
#pragma warning restore CA1848, CA2254
            }
        }
        finally
        {
#pragma warning disable TUnit0055 // Restores the writer this test replaced.
            Console.SetOut(stdout);
#pragma warning restore TUnit0055
        }

        var output = console.ToString();
        await Assert.That(output).DoesNotContain(ExceptionSecret);
        await Assert.That(output).Contains("InvalidOperationException");

        // Records may carry a journald <N> prefix, and other tests may write to the same console
        // while this one runs; only lines holding a JSON object are read.
        var record = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains('{', StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line[line.IndexOf('{', StringComparison.Ordinal)..]).RootElement)
            .Single(json => json.GetProperty("msg").GetString()?.Contains("Token exchange failed", StringComparison.Ordinal) == true);
        await Assert.That(record.TryGetProperty("level", out _)).IsTrue();
    }
}
