using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace TerraScale.Logging.Tests;

public sealed class RecordFormatTests
{
    private const string FallbackService = "fallback";

    [Test]
    [Arguments(LogLevel.Trace, "DEBUG")]
    [Arguments(LogLevel.Debug, "DEBUG")]
    [Arguments(LogLevel.Information, "INFO")]
    [Arguments(LogLevel.Warning, "WARN")]
    [Arguments(LogLevel.Error, "ERROR")]
    [Arguments(LogLevel.Critical, "ERROR")]
    public async Task LevelsMapToRecordLevels(LogLevel level, string expected)
    {
        var line = Write(logger => logger.Log(level, "Level check"));
        await Assert.That(Parse(line).GetProperty("level").GetString()).IsEqualTo(expected);
    }

    [Test]
    public async Task KeysAppearInContractOrder()
    {
        Activity.Current = null;
        var line = Write(
            logger =>
            {
                using var scope = logger.BeginScope(new Dictionary<string, object> { ["request"] = "r1" });
                logger.LogWarning(new EventId(42), new InvalidOperationException("boom"), "Widget {Id} is slow", 7);
            },
            ("TS_SERVICE", "orchestrator"), ("TS_REGION", "us-ashburn-1"), ("TS_NODE", "node-1"));

        await Assert.That(string.Join(',', Keys(Parse(line))))
            .IsEqualTo("time,level,msg,service,region,node,component,event_id,Id,request,error,stack");
    }

    [Test]
    public async Task TemplateIsMessageAndStateKeepsJsonTypes()
    {
        var line = Write(logger => logger.LogInformation("Widget {Id} created by {Owner} {Ratio} {Ok} {Missing}", 7, new Owner("ops"), 0.5, true, null));
        var root = Parse(line);

        await Assert.That(root.GetProperty("msg").GetString()).IsEqualTo("Widget {Id} created by {Owner} {Ratio} {Ok} {Missing}");
        await Assert.That(root.GetProperty("Id").GetInt32()).IsEqualTo(7);
        await Assert.That(root.GetProperty("Owner").GetString()).IsEqualTo("ops");
        await Assert.That(root.GetProperty("Ratio").GetDouble()).IsEqualTo(0.5);
        await Assert.That(root.GetProperty("Ok").ValueKind).IsEqualTo(JsonValueKind.True);
        await Assert.That(root.GetProperty("Missing").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(root.TryGetProperty("{OriginalFormat}", out _)).IsFalse();
    }

    [Test]
    public async Task FormattedMessageIsUsedWithoutTemplate()
    {
        var root = Parse(Write(logger => logger.Log(LogLevel.Information, new EventId(0), "state", null, (state, _) => "custom text")));

        await Assert.That(root.GetProperty("msg").GetString()).IsEqualTo("custom text");
    }

    [Test]
    public async Task ServiceFallsBackAndEmptyEnvironmentFieldsAreOmitted()
    {
        var root = Parse(Write(logger => logger.LogInformation("Hello"), ("TS_REGION", "us-ashburn-1")));

        await Assert.That(root.GetProperty("service").GetString()).IsEqualTo(FallbackService);
        await Assert.That(root.GetProperty("region").GetString()).IsEqualTo("us-ashburn-1");
        await Assert.That(root.TryGetProperty("node", out _)).IsFalse();
    }

    [Test]
    public async Task EnvironmentServiceOverridesFallback()
    {
        var root = Parse(Write(logger => logger.LogInformation("Hello"), ("TS_SERVICE", "orchestrator")));

        await Assert.That(root.GetProperty("service").GetString()).IsEqualTo("orchestrator");
    }

    [Test]
    public async Task ActivityTraceIdsComeFromActivityCurrent()
    {
        using var activity = new Activity("work").Start();
        var root = Parse(Write(logger => logger.LogInformation("Hello")));

        await Assert.That(root.GetProperty("trace_id").GetString()).IsEqualTo(activity.TraceId.ToHexString());
        await Assert.That(root.GetProperty("span_id").GetString()).IsEqualTo(activity.SpanId.ToHexString());
    }

    // ZLogger formats on the calling thread, so the record reads the caller's Activity even though the output is a stream.
    [Test]
    public async Task ZLogCallInsideActivityRecordsItsTraceId()
    {
        using var activity = new Activity("work").Start();
        var root = Parse(Write(logger => logger.ZLogInformation($"Hello")));

        await Assert.That(root.GetProperty("trace_id").GetString()).IsEqualTo(activity.TraceId.ToHexString());
        await Assert.That(root.GetProperty("span_id").GetString()).IsEqualTo(activity.SpanId.ToHexString());
    }

    [Test]
    public async Task ExceptionBecomesErrorAndStack()
    {
        var exception = new InvalidOperationException("boom");
        var root = Parse(Write(logger => logger.LogError(exception, "Failed")));

        await Assert.That(root.GetProperty("error").GetString()).IsEqualTo("System.InvalidOperationException: boom");
        await Assert.That(root.GetProperty("stack").GetString()).IsEqualTo(exception.ToString());
    }

    [Test]
    public async Task CollidingKeysArePrefixedAndRepeatedKeysKeepTheFirstValue()
    {
        Activity.Current = null;
        var line = Write(
            logger =>
            {
                using var scope = logger.BeginScope(new Dictionary<string, object> { ["id"] = 3, ["trace_id"] = "scope" });
                logger.LogInformation("{service} {id} {id}", "state", 1, 2);
            },
            ("TS_SERVICE", "orchestrator"));
        var root = Parse(line);

        await Assert.That(string.Join(',', Keys(root))).IsEqualTo("time,level,msg,service,component,attr.service,id,attr.trace_id");
        await Assert.That(root.GetProperty("attr.service").GetString()).IsEqualTo("state");
        await Assert.That(root.GetProperty("id").GetInt32()).IsEqualTo(1);
    }

    [Test]
    [Arguments(LogLevel.Error, "<3>")]
    [Arguments(LogLevel.Warning, "<4>")]
    [Arguments(LogLevel.Information, "<5>")]
    [Arguments(LogLevel.Debug, "<7>")]
    public async Task JournalStreamPrefixesSyslogPriority(LogLevel level, string priority)
    {
        var line = Write(logger => logger.Log(level, "Journal line"), ("JOURNAL_STREAM", "8:12345"));

        await Assert.That(line.StartsWith(priority, StringComparison.Ordinal)).IsTrue();
        await Assert.That(Parse(line[priority.Length..]).GetProperty("level").GetString()).IsNotNull();
    }

    [Test]
    public async Task WithoutJournalStreamThereIsNoPrefix()
    {
        var line = Write(logger => logger.LogError("No journal"));

        await Assert.That(line.StartsWith('{')).IsTrue();
    }

    [Test]
    public async Task EachRecordIsOneLineAndNewlinesRoundTripEscaped()
    {
        const string text = "first\nsecond\r\nthird\u2028";
        var line = Write(logger => logger.LogInformation("multi\nline {Text}", text));

        await Assert.That(line.Contains('\n')).IsFalse();
        await Assert.That(line.Contains('\r')).IsFalse();
        await Assert.That(Parse(line).GetProperty("Text").GetString()).IsEqualTo(text);
    }

    [Test]
    public async Task PlainStringScopeIsWrittenAsScope()
    {
        var root = Parse(Write(logger =>
        {
            using (logger.BeginScope("request-7"))
            {
                logger.LogInformation("in scope");
            }
        }));

        await Assert.That(root.GetProperty("Scope").GetString()).IsEqualTo("request-7");
    }

    // Consumers capture records with Console.SetOut, so the provider must read Console.Out at each write.
#pragma warning disable TUnit0055 // The writer is restored in finally, before the test ends.
#pragma warning restore TUnit0055

    // The provider writes a newline after every record; the helper drops it so assertions see the JSON alone.
    private static string Write(Action<ILogger> write, params (string Key, string Value)[] environment) =>
        LogHarness.Capture(write, environment).TrimEnd('\n');

    private static JsonElement Parse(string line) => JsonElement.Parse(line);

    private static List<string> Keys(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToList();

    private sealed class Owner(string name)
    {
        public override string ToString() => name;
    }

    // Throws once, like a stdout pipe that broke, then accepts writes.
    private sealed class FailingOnceWriter : StringWriter
    {
        private bool _failed;

        public override void Write(ReadOnlySpan<char> buffer)
        {
            if (!_failed)
            {
                _failed = true;
                throw new IOException("Broken pipe");
            }

            base.Write(buffer);
        }
    }

    private sealed class ThrowingText
    {
        public override string ToString() => throw new InvalidOperationException("no text");
    }
}
