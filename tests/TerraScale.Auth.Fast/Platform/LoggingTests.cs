using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraScale.Auth.Hosting;
using TerraScale.Auth.Tests;
using TerraScale.Logging;
using ZLogger;

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
                var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TerraScale.Auth.Tests");
                var clientId = "client-1";
                var user = "alice";
                logger.ZLogError(new InvalidOperationException(ExceptionSecret), $"Token exchange failed for {clientId:@ClientId}");
                logger.ZLogError(new InvalidOperationException($"password={LogSecrets.FakeSecret}"), $"Sign-in failed for {user:@User}");
                logger.ZLogInformation($"Upstream said {new LogSecrets.UpstreamReply(LogSecrets.FakeSecret):@Reply}");
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
        await Assert.That(output).DoesNotContain(LogSecrets.FakeSecret);
        await Assert.That(output).Contains("InvalidOperationException");
        await Assert.That(output).Contains("\"Reply\":\"[UpstreamReply withheld]\"");

        // Records may carry a journald <N> prefix, and other tests may write to the same console
        // while this one runs; only lines holding a JSON object are read.
        var record = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains('{', StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line[line.IndexOf('{', StringComparison.Ordinal)..]).RootElement)
            .Single(json => json.GetProperty("msg").GetString()?.Contains("Sign-in failed for {User}", StringComparison.Ordinal) == true);
        await Assert.That(record.TryGetProperty("level", out _)).IsTrue();
        await Assert.That(record.GetProperty("User").GetString()).IsEqualTo("alice");
    }
}
