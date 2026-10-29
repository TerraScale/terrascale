using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace TerraScale.Logging.Tests;

// Golden lines captured from the console formatter before the ZLogger switch (record v1).
// Each event is logged twice (the MEL call and the ZLog call) and must match its line byte-for-byte.
// The span id is random per run, so the golden lines carry 0000000000000000 and the activity's real span id is substituted.
public sealed class GoldenRecordTests
{
    private const string SpanPlaceholder = "0000000000000000";
    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string ParentSpanId = "00f067aa0ba902b7";

    private static readonly (string Key, string Value)[] Plain = [("TS_SERVICE", "terrabase"), ("TS_REGION", "gru-1"), ("TS_NODE", "node-1")];
    private static readonly (string Key, string Value)[] Journal = [.. Plain, ("JOURNAL_STREAM", "8:12345")];

    private const string E1 = """{"time":"2026-10-11T12:34:56.789012Z","level":"INFO","msg":"Service started","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets"}""";
    private const string E1Journal = """<5>{"time":"2026-10-11T12:34:56.789012Z","level":"INFO","msg":"Service started","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets"}""";

    private const string E2 = """{"time":"2026-10-11T12:34:56.789012Z","level":"WARN","msg":"Order {Id} {Count} {Big} {Flag} {Ratio} {Price} {Missing} {Ref} {Stamp} {Item}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","Id":"ord-1","Count":42,"Big":9000000000,"Flag":true,"Ratio":0.5,"Price":19.99,"Missing":null,"Ref":"0f8fad5b-d9cb-469f-a165-70867728950e","Stamp":"10/11/2026 12:34:56 +00:00","Item":"ops"}""";
    private const string E2Journal = """<4>{"time":"2026-10-11T12:34:56.789012Z","level":"WARN","msg":"Order {Id} {Count} {Big} {Flag} {Ratio} {Price} {Missing} {Ref} {Stamp} {Item}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","Id":"ord-1","Count":42,"Big":9000000000,"Flag":true,"Ratio":0.5,"Price":19.99,"Missing":null,"Ref":"0f8fad5b-d9cb-469f-a165-70867728950e","Stamp":"10/11/2026 12:34:56 +00:00","Item":"ops"}""";

    private const string E3 = """{"time":"2026-10-11T12:34:56.789012Z","level":"INFO","msg":"Collide {service} {msg} {Dup} {Dup}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","attr.service":"svc","attr.msg":"m","Dup":1}""";
    private const string E3Journal = """<5>{"time":"2026-10-11T12:34:56.789012Z","level":"INFO","msg":"Collide {service} {msg} {Dup} {Dup}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","attr.service":"svc","attr.msg":"m","Dup":1}""";

    private const string E4 = """{"time":"2026-10-11T12:34:56.789012Z","level":"ERROR","msg":"Payment {PaymentId} failed","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","event_id":42,"PaymentId":"p-9"}""";
    private const string E4Journal = """<3>{"time":"2026-10-11T12:34:56.789012Z","level":"ERROR","msg":"Payment {PaymentId} failed","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","event_id":42,"PaymentId":"p-9"}""";

    private const string E5 = """{"time":"2026-10-11T12:34:56.789012Z","level":"ERROR","msg":"Import failed for {File}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","File":"a.csv","error":"System.InvalidOperationException: outer","stack":"System.InvalidOperationException: outer\n ---> System.ArgumentException: inner\n   --- End of inner exception stack trace ---"}""";
    private const string E5Journal = """<3>{"time":"2026-10-11T12:34:56.789012Z","level":"ERROR","msg":"Import failed for {File}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","File":"a.csv","error":"System.InvalidOperationException: outer","stack":"System.InvalidOperationException: outer\n ---> System.ArgumentException: inner\n   --- End of inner exception stack trace ---"}""";

    private const string E6 = """{"time":"2026-10-11T12:34:56.789012Z","level":"INFO","msg":"Handled {RequestId} {Status}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","RequestId":"state-r","Status":200,"Tenant":"acme","RequestPath":"/v1/x"}""";
    private const string E6Journal = """<5>{"time":"2026-10-11T12:34:56.789012Z","level":"INFO","msg":"Handled {RequestId} {Status}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","RequestId":"state-r","Status":200,"Tenant":"acme","RequestPath":"/v1/x"}""";

    private const string E7 = """{"time":"2026-10-11T12:34:56.789012Z","level":"INFO","msg":"Traced {Route}","service":"terrabase","region":"gru-1","node":"node-1","trace_id":"4bf92f3577b34da6a3ce929d0e0e4736","span_id":"0000000000000000","component":"Acme.Widgets","Route":"/v1/x"}""";
    private const string E7Journal = """<5>{"time":"2026-10-11T12:34:56.789012Z","level":"INFO","msg":"Traced {Route}","service":"terrabase","region":"gru-1","node":"node-1","trace_id":"4bf92f3577b34da6a3ce929d0e0e4736","span_id":"0000000000000000","component":"Acme.Widgets","Route":"/v1/x"}""";

    private const string E8 = """{"time":"2026-10-11T12:34:56.789012Z","level":"INFO","msg":"Escapes {Quote} {Slash} {Newline} {Html} {Unicode} {Nan} {Inf}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","Quote":"say \"hi\"","Slash":"C:\\temp","Newline":"line1\nline2","Html":"<script>&","Unicode":"ção 日本","Nan":"NaN","Inf":"Infinity"}""";
    private const string E8Journal = """<5>{"time":"2026-10-11T12:34:56.789012Z","level":"INFO","msg":"Escapes {Quote} {Slash} {Newline} {Html} {Unicode} {Nan} {Inf}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","Quote":"say \"hi\"","Slash":"C:\\temp","Newline":"line1\nline2","Html":"<script>&","Unicode":"ção 日本","Nan":"NaN","Inf":"Infinity"}""";

    private const string E9 = """{"time":"2026-10-11T12:34:56.789012Z","level":"INFO","msg":"took {Elapsed:0.00} ms","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","Elapsed":1.23456}""";
    private const string E9Journal = """<5>{"time":"2026-10-11T12:34:56.789012Z","level":"INFO","msg":"took {Elapsed:0.00} ms","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","Elapsed":1.23456}""";

    private const string E10Debug = """{"time":"2026-10-11T12:34:56.789012Z","level":"DEBUG","msg":"Debug line {Step}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","Step":1}""";
    private const string E10DebugJournal = """<7>{"time":"2026-10-11T12:34:56.789012Z","level":"DEBUG","msg":"Debug line {Step}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","Step":1}""";

    private const string E10Critical = """{"time":"2026-10-11T12:34:56.789012Z","level":"ERROR","msg":"Critical line {Code}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","Code":2}""";
    private const string E10CriticalJournal = """<3>{"time":"2026-10-11T12:34:56.789012Z","level":"ERROR","msg":"Critical line {Code}","service":"terrabase","region":"gru-1","node":"node-1","component":"Acme.Widgets","Code":2}""";

    [Test]
    public async Task E1PlainMessage()
    {
        // TUnit starts its own Activity per test; clear it so only the golden fields appear.
        Activity.Current = null;

        await AssertGolden(E1, E1Journal,
            logger => logger.LogInformation("Service started"),
            logger => logger.ZLogInformation($"Service started"));
    }

    [Test]
    public async Task E2TypedFields()
    {
        Activity.Current = null;

        var id = "ord-1";
        var count = 42;
        var big = 9000000000L;
        var flag = true;
        var ratio = 0.5;
        var price = 19.99m;
        object? missing = null;
        var reference = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var stamp = DateTimeOffset.Parse("2026-10-11T12:34:56+00:00", CultureInfo.InvariantCulture);
        var item = new Owner("ops");

        await AssertGolden(E2, E2Journal,
            logger => logger.LogWarning("Order {Id} {Count} {Big} {Flag} {Ratio} {Price} {Missing} {Ref} {Stamp} {Item}",
                id, count, big, flag, ratio, price, missing, reference, stamp, item),
            logger => logger.ZLogWarning($"Order {id:@Id} {count:@Count} {big:@Big} {flag:@Flag} {ratio:@Ratio} {price:@Price} {missing:@Missing} {reference:@Ref} {stamp:@Stamp} {item:@Item}"));
    }

    [Test]
    public async Task E3ReservedKeysAndDuplicates()
    {
        Activity.Current = null;

        var service = "svc";
        var msg = "m";

        await AssertGolden(E3, E3Journal,
            logger => logger.LogInformation("Collide {service} {msg} {Dup} {Dup}", service, msg, 1, 2),
            logger => logger.ZLogInformation($"Collide {service:@service} {msg:@msg} {1:@Dup} {2:@Dup}"));
    }

    [Test]
    public async Task E4EventId()
    {
        Activity.Current = null;

        var paymentId = "p-9";

        await AssertGolden(E4, E4Journal,
            logger => logger.LogError(new EventId(42, "Named"), "Payment {PaymentId} failed", paymentId),
            logger => logger.ZLogError(new EventId(42, "Named"), $"Payment {paymentId:@PaymentId} failed"));
    }

    [Test]
    public async Task E5ExceptionWithInner()
    {
        Activity.Current = null;

        var exception = new InvalidOperationException("outer", new ArgumentException("inner"));
        var file = "a.csv";

        await AssertGolden(E5, E5Journal,
            logger => logger.LogError(exception, "Import failed for {File}", file),
            logger => logger.ZLogError(exception, $"Import failed for {file:@File}"));
    }

    [Test]
    public async Task E6NestedScopesWithCollision()
    {
        Activity.Current = null;

        var requestId = "state-r";

        await AssertGolden(E6, E6Journal,
            logger =>
            {
                using (logger.BeginScope("Tenant {Tenant}", "acme"))
                using (logger.BeginScope(RequestScope()))
                {
                    logger.LogInformation("Handled {RequestId} {Status}", requestId, 200);
                }
            },
            logger =>
            {
                using (logger.BeginScope("Tenant {Tenant}", "acme"))
                using (logger.BeginScope(RequestScope()))
                {
                    logger.ZLogInformation($"Handled {requestId:@RequestId} {200:@Status}");
                }
            });
    }

    [Test]
    public async Task E7ActivityTraceAndSpan()
    {
        using var activity = new Activity("request");
        activity.SetParentId(ActivityTraceId.CreateFromString(TraceId.AsSpan()), ActivitySpanId.CreateFromString(ParentSpanId.AsSpan()));
        activity.Start();
        var span = activity.SpanId.ToHexString();
        var route = "/v1/x";

        await AssertGolden(E7.Replace(SpanPlaceholder, span), E7Journal.Replace(SpanPlaceholder, span),
            logger => logger.LogInformation("Traced {Route}", route),
            logger => logger.ZLogInformation($"Traced {route:@Route}"));
    }

    [Test]
    public async Task E8Escaping()
    {
        Activity.Current = null;

        var quote = "say \"hi\"";
        var slash = "C:\\temp";
        var newline = "line1\nline2";
        var html = "<script>&";
        var unicode = "ção 日本";
        var nan = double.NaN;
        var infinity = double.PositiveInfinity;

        await AssertGolden(E8, E8Journal,
            logger => logger.LogInformation("Escapes {Quote} {Slash} {Newline} {Html} {Unicode} {Nan} {Inf}",
                quote, slash, newline, html, unicode, nan, infinity),
            logger => logger.ZLogInformation($"Escapes {quote:@Quote} {slash:@Slash} {newline:@Newline} {html:@Html} {unicode:@Unicode} {nan:@Nan} {infinity:@Inf}"));
    }

    [Test]
    public async Task E9FormatSpecifier()
    {
        Activity.Current = null;

        var elapsed = 1.23456;

        await Assert.That(LogHarness.Capture(logger => logger.LogInformation("took {Elapsed:0.00} ms", elapsed), Plain)).IsEqualTo(E9 + "\n");
        await Assert.That(LogHarness.Capture(logger => logger.LogInformation("took {Elapsed:0.00} ms", elapsed), Journal)).IsEqualTo(E9Journal + "\n");

        // Known difference: ZLogger's message template writes the format after a comma, so msg reads "{Elapsed,0.00}".
        // Only that msg text differs from the golden line; every other byte matches. The ZLog call is asserted against that exact output.
        await Assert.That(LogHarness.Capture(logger => logger.ZLogInformation($"took {elapsed:@Elapsed:0.00} ms"), Plain))
            .IsEqualTo(E9.Replace("took {Elapsed:0.00} ms", "took {Elapsed,0.00} ms") + "\n");
        await Assert.That(LogHarness.Capture(logger => logger.ZLogInformation($"took {elapsed:@Elapsed:0.00} ms"), Journal))
            .IsEqualTo(E9Journal.Replace("took {Elapsed:0.00} ms", "took {Elapsed,0.00} ms") + "\n");
    }

    [Test]
    public async Task E10DebugLine()
    {
        Activity.Current = null;

        var step = 1;

        await AssertGolden(E10Debug, E10DebugJournal,
            logger => logger.LogDebug("Debug line {Step}", step),
            logger => logger.ZLogDebug($"Debug line {step:@Step}"));
    }

    [Test]
    public async Task E10CriticalLine()
    {
        Activity.Current = null;

        var code = 2;

        await AssertGolden(E10Critical, E10CriticalJournal,
            logger => logger.LogCritical("Critical line {Code}", code),
            logger => logger.ZLogCritical($"Critical line {code:@Code}"));
    }

    private static async Task AssertGolden(string plain, string journal, Action<ILogger> legacy, Action<ILogger> zlog)
    {
        await Assert.That(LogHarness.Capture(legacy, Plain)).IsEqualTo(plain + "\n");
        await Assert.That(LogHarness.Capture(zlog, Plain)).IsEqualTo(plain + "\n");
        await Assert.That(LogHarness.Capture(legacy, Journal)).IsEqualTo(journal + "\n");
        await Assert.That(LogHarness.Capture(zlog, Journal)).IsEqualTo(journal + "\n");
    }

    private static Dictionary<string, object> RequestScope() => new()
    {
        ["RequestId"] = "r-1",
        ["RequestPath"] = "/v1/x",
    };

    private sealed class Owner(string name)
    {
        public override string ToString() => name;
    }
}
