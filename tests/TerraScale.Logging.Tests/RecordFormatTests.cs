using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        var line = Write(Formatter(), level, []);
        await Assert.That(Parse(line).GetProperty("level").GetString()).IsEqualTo(expected);
    }

    [Test]
    public async Task KeysAppearInContractOrder()
    {
        Activity.Current = null;
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(new List<KeyValuePair<string, object?>> { new("request", "r1") });
        var line = Write(
            Formatter(("TS_SERVICE", "orchestrator"), ("TS_REGION", "us-ashburn-1"), ("TS_NODE", "node-1")),
            LogLevel.Warning,
            [new("{OriginalFormat}", "Widget {Id} is slow"), new("Id", 7)],
            "Widget 7 is slow",
            new InvalidOperationException("boom"),
            eventId: 42,
            scopes: scopes);

        await Assert.That(string.Join(',', Keys(Parse(line))))
            .IsEqualTo("time,level,msg,service,region,node,component,event_id,Id,request,error,stack");
    }

    [Test]
    public async Task TemplateIsMessageAndStateKeepsJsonTypes()
    {
        var line = Write(Formatter(), LogLevel.Information,
            [new("{OriginalFormat}", "Widget {Id} created by {Owner}"), new("Id", 7), new("Owner", new Owner("ops")), new("Ratio", 0.5), new("Ok", true), new("Missing", null)],
            "Widget 7 created by ops");
        var root = Parse(line);

        await Assert.That(root.GetProperty("msg").GetString()).IsEqualTo("Widget {Id} created by {Owner}");
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
        var root = Parse(Write(Formatter(), LogLevel.Information, [new("Id", 7)], "custom text"));

        await Assert.That(root.GetProperty("msg").GetString()).IsEqualTo("custom text");
    }

    [Test]
    public async Task ServiceFallsBackAndEmptyEnvironmentFieldsAreOmitted()
    {
        var root = Parse(Write(Formatter(("TS_REGION", "us-ashburn-1")), LogLevel.Information, []));

        await Assert.That(root.GetProperty("service").GetString()).IsEqualTo(FallbackService);
        await Assert.That(root.GetProperty("region").GetString()).IsEqualTo("us-ashburn-1");
        await Assert.That(root.TryGetProperty("node", out _)).IsFalse();
    }

    [Test]
    public async Task EnvironmentServiceOverridesFallback()
    {
        var root = Parse(Write(Formatter(("TS_SERVICE", "orchestrator")), LogLevel.Information, []));

        await Assert.That(root.GetProperty("service").GetString()).IsEqualTo("orchestrator");
    }

    [Test]
    public async Task ActivityTraceIdsComeFromActivityCurrent()
    {
        using var activity = new Activity("work").Start();
        var root = Parse(Write(Formatter(), LogLevel.Information, []));

        await Assert.That(root.GetProperty("trace_id").GetString()).IsEqualTo(activity.TraceId.ToHexString());
        await Assert.That(root.GetProperty("span_id").GetString()).IsEqualTo(activity.SpanId.ToHexString());
    }

    [Test]
    public async Task ExceptionBecomesErrorAndStack()
    {
        var exception = new InvalidOperationException("boom");
        var root = Parse(Write(Formatter(), LogLevel.Error, [], exception: exception));

        await Assert.That(root.GetProperty("error").GetString()).IsEqualTo("System.InvalidOperationException: boom");
        await Assert.That(root.GetProperty("stack").GetString()).IsEqualTo(exception.ToString());
    }

    [Test]
    public async Task CollidingKeysArePrefixedAndRepeatedKeysKeepTheFirstValue()
    {
        Activity.Current = null;
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(new List<KeyValuePair<string, object?>> { new("id", 3), new("trace_id", "scope") });
        var line = Write(
            Formatter(("TS_SERVICE", "orchestrator")),
            LogLevel.Information,
            [new("service", "state"), new("id", 1), new("id", 2)],
            scopes: scopes);
        var root = Parse(line);

        await Assert.That(string.Join(',', Keys(root))).IsEqualTo("time,level,msg,service,component,attr.service,id,attr.trace_id");
        await Assert.That(root.GetProperty("attr.service").GetString()).IsEqualTo("state");
        await Assert.That(root.GetProperty("id").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task ThrowingToStringBecomesUnformattableMarker()
    {
        var root = Parse(Write(Formatter(), LogLevel.Information, [new("Bad", new ThrowingText())]));

        await Assert.That(root.GetProperty("Bad").GetString()).IsEqualTo("<unformattable>");
        await Assert.That(root.GetProperty("level").GetString()).IsEqualTo("INFO");
    }

    [Test]
    [Arguments(LogLevel.Error, "<3>")]
    [Arguments(LogLevel.Warning, "<4>")]
    [Arguments(LogLevel.Information, "<5>")]
    [Arguments(LogLevel.Debug, "<7>")]
    public async Task JournalStreamPrefixesSyslogPriority(LogLevel level, string priority)
    {
        var line = Write(Formatter(("JOURNAL_STREAM", "8:12345")), level, []);

        await Assert.That(line.StartsWith(priority, StringComparison.Ordinal)).IsTrue();
        await Assert.That(Parse(line[priority.Length..]).GetProperty("level").GetString()).IsNotNull();
    }

    [Test]
    public async Task WithoutJournalStreamThereIsNoPrefix()
    {
        var line = Write(Formatter(), LogLevel.Error, []);

        await Assert.That(line.StartsWith('{')).IsTrue();
    }

    [Test]
    public async Task EachRecordIsOneLineAndNewlinesRoundTripEscaped()
    {
        const string text = "first\nsecond\r\nthird\u2028";
        var line = Write(Formatter(), LogLevel.Information, [new("Text", text)], "multi\nline");
        var body = line.TrimEnd('\r', '\n');

        await Assert.That(body.Contains('\n')).IsFalse();
        await Assert.That(body.Contains('\r')).IsFalse();
        await Assert.That(Parse(line).GetProperty("Text").GetString()).IsEqualTo(text);
    }

    private static TerraScaleConsoleFormatter Formatter(params (string Key, string Value)[] environment)
    {
        var values = environment.ToDictionary(pair => pair.Key, pair => pair.Value);
        return new TerraScaleConsoleFormatter(
            Options.Create(new TerraScaleFormatterOptions { Service = FallbackService }),
            key => values.GetValueOrDefault(key));
    }

    private static string Write(
        TerraScaleConsoleFormatter formatter,
        LogLevel level,
        IReadOnlyList<KeyValuePair<string, object?>> state,
        string formatted = "formatted text",
        Exception? exception = null,
        int eventId = 0,
        IExternalScopeProvider? scopes = null)
    {
        var writer = new StringWriter();
        formatter.Write(
            new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(level, "Acme.Widgets", new EventId(eventId), state, exception, (_, _) => formatted),
            scopes,
            writer);
        return writer.ToString();
    }

    private static JsonElement Parse(string line) => JsonElement.Parse(line);

    private static List<string> Keys(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToList();

    private sealed class Owner(string name)
    {
        public override string ToString() => name;
    }

    private sealed class ThrowingText
    {
        public override string ToString() => throw new InvalidOperationException("no text");
    }
}
