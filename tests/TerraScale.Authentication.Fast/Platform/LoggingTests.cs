using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Tests;
using TerraScale.Logging;
using ZLogger;

namespace TerraScale.Authentication.Fast.Platform;

// Records go to a writer this test owns, never the process-wide Console.Out, so it runs beside every other test.
public sealed class LoggingTests
{
    private const string ExceptionSecret = "exception-secret-4d1e7a";

    [Test]
    public async Task Console_records_are_json_and_withhold_exception_messages()
    {
        using var console = new StringWriter();
        var builder = Host.CreateApplicationBuilder();
        // The stdout pipeline AddTerraScaleLogging builds, writing to the test's writer instead of Console.Out.
        builder.Logging.AddTerraScaleLogging("ts-auth", console, _ => null, TimeProvider.System);
        builder.Services.AddSafeLogging();
        using (var host = builder.Build())
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TerraScale.Authentication.Tests");
            var clientId = "client-1";
            var user = "alice";
            logger.ZLogError(new InvalidOperationException(ExceptionSecret), $"Token exchange failed for {clientId:@ClientId}");
            logger.ZLogError(new InvalidOperationException($"password={LogSecrets.FakeSecret}"), $"Sign-in failed for {user:@User}");
            logger.ZLogInformation($"Upstream said {new LogSecrets.UpstreamReply(LogSecrets.FakeSecret):@Reply}");
        }

        var output = console.ToString();
        await Assert.That(output).DoesNotContain(ExceptionSecret);
        await Assert.That(output).DoesNotContain(LogSecrets.FakeSecret);
        await Assert.That(output).Contains("InvalidOperationException");
        await Assert.That(output).Contains("\"Reply\":\"[UpstreamReply withheld]\"");

        // Only lines holding a JSON object are read; host startup records come first.
        var record = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains('{', StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line[line.IndexOf('{', StringComparison.Ordinal)..]).RootElement)
            .Single(json => json.GetProperty("msg").GetString()?.Contains("Sign-in failed for {User}", StringComparison.Ordinal) == true);
        await Assert.That(record.TryGetProperty("level", out _)).IsTrue();
        await Assert.That(record.GetProperty("User").GetString()).IsEqualTo("alice");
    }
}
