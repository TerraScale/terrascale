using Microsoft.Extensions.Logging;

namespace TerraScale.Logging.Tests;

/// <summary>Logs through the real provider into a writer, with a fixed clock and environment.</summary>
internal static class LogHarness
{
    // 2026-10-11T12:34:56.789012Z: the golden records use this time.
    public static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 10, 11, 12, 34, 56, TimeSpan.Zero).AddTicks(7_890_120));

    public static ILoggerFactory CreateFactory(TextWriter output, params (string Key, string Value)[] environment)
    {
        var values = environment.ToDictionary(pair => pair.Key, pair => pair.Value);
        return LoggerFactory.Create(builder => builder.AddTerraScaleLogging(
            "fallback",
            output,
            key => values.GetValueOrDefault(key),
            Clock,
            ("Acme.Widgets", LogLevel.Trace)));
    }

    /// <summary>Runs <paramref name="write"/> on a fresh provider and returns everything it wrote.</summary>
    public static string Capture(Action<ILogger> write, params (string Key, string Value)[] environment)
    {
        using var output = new StringWriter();
        using (var factory = CreateFactory(output, environment))
        {
            write(factory.CreateLogger("Acme.Widgets"));
        }

        return output.ToString();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
