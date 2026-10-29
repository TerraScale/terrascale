using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.Authentication.Hosting;
using ZLogger;

namespace TerraScale.Authentication.Slow.Security;

/// <summary>
/// ZLogger call sites logged at the same time on many threads keep their own template, names and formats through the
/// safe-logging wrapper. Main run 38282841575 recorded <c>Took {User} s</c> (another call site's name, its <c>0.00</c>
/// format lost) while LoggingTests logged <c>Sign-in failed for {User}</c> concurrently.
/// </summary>
public sealed class ZLoggerConcurrencyTests
{
    [Test]
    public async Task Concurrent_call_sites_keep_their_own_template_names_and_formats()
    {
        var lines = new ConcurrentQueue<string>();
        using var services = new ServiceCollection().AddLogging(builder => builder.AddProvider(new Capture(lines))).AddSafeLogging().BuildServiceProvider();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ZLoggerConcurrency");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var user = "alice";
            for (var i = 0; i < 2000; i++)
            {
                if ((i + worker) % 2 == 0)
                {
                    logger.ZLogInformation($"Sign-in failed for {user:@User}");
                }
                else
                {
                    logger.ZLogInformation($"Took {1.5:@Elapsed:0.00} s");
                }
            }
        })));

        var wrong = lines.Where(line => line != "Sign-in failed for alice | {OriginalFormat}=Sign-in failed for {User}"
            && !(line.StartsWith("Took 1.50 s | ", StringComparison.Ordinal) && line.Contains("{OriginalFormat}=Took {Elapsed", StringComparison.Ordinal))).Take(3).ToList();
        await Assert.That(lines.Count).IsEqualTo(16_000);
        await Assert.That(wrong).IsEmpty();
    }

    private sealed class Capture(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(lines);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var template = state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.LastOrDefault(pair => pair.Key == "{OriginalFormat}").Value : null;
                lines.Enqueue($"{formatter(state, exception)} | {{OriginalFormat}}={template}");
            }
        }
    }
}
