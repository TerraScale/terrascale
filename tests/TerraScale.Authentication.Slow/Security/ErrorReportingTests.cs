using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Tests;
using ZLogger;

namespace TerraScale.Authentication.Slow.Security;

[NotInParallel(nameof(ErrorReportingTests))]
public sealed class ErrorReportingTests
{
    private const string BearerSecret = "bearer-secret-7f3a9c";
    private const string CookieSecret = "cookie-secret-5b21de";
    private const string QuerySecret = "query-secret-90c4aa";
    private const string FormSecret = "form-secret-41e8bb";
    private const string MessageSecret = "message-secret-c07d11";
    private const string InnerSecret = "inner-secret-6a93f2";
    private const string UrlSecret = "url-secret-2d5e77";
    private const string HeaderSecret = "header-secret-8e14ab";
    private const string JsonSecret = "json-secret-3c9f50";
    private const string UserInfoSecret = "userinfo-secret-7b6d21";
    private const string TagSecret = "tag-secret-0fa3c8";
    private const string ExtraSecret = "extra-secret-d4e2b9";
    private const string BreadcrumbSecret = "breadcrumb-secret-91c7e6";
    private const string ContextSecret = "context-secret-5e0b3a";
    private const string StreamSecret = "stream-secret-a81f4c";
    private const string FrameSecret = "frame-secret-7c2e19";
    private const string AlphanumericSecret = "token7c2e19secret";

    private static readonly string[] AllSecrets =
    [
        BearerSecret, CookieSecret, QuerySecret, FormSecret, MessageSecret, InnerSecret, UrlSecret,
        HeaderSecret, JsonSecret, UserInfoSecret, TagSecret, ExtraSecret, BreadcrumbSecret, ContextSecret, FrameSecret, AlphanumericSecret, "203.0.113.7",
        // Exception messages are withheld entirely, whatever they contain.
        "synthetic failure", "upstream rejected",
    ];

    // A failure whose messages, scope and breadcrumbs carry every kind of credential.
    private static IResult Crash()
    {
        Sentry.SentrySdk.ConfigureScope(scope =>
        {
            scope.SetTag("session", TagSecret);
            scope.SetTag("component", $"passwd={TagSecret}");
            scope.SetTag("RequestId", TagSecret);
            scope.TransactionName = $"transaction {TagSecret}";
            scope.SetExtra("passwd", ExtraSecret);
            scope.SetExtra("context", new { password = ExtraSecret });
            scope.Contexts["upstream"] = new { password = ContextSecret };
            scope.AddEventProcessor(new FrameSecretPlanter());
        });
        Sentry.SentrySdk.AddBreadcrumb(
            $"calling https://alice:{UserInfoSecret}@idp.example/token with session={BreadcrumbSecret}",
            data: new Dictionary<string, string> { ["session"] = BreadcrumbSecret, ["url"] = $"https://idp.example/cb?code={BreadcrumbSecret}" });
        throw new InvalidOperationException(
            $"synthetic failure calling https://idp.example/callback?code={UrlSecret}&state=1 with password={MessageSecret}; "
            + $"Authorization: Bearer {HeaderSecret}; body {{\"password\":\"{JsonSecret}\"}}; upstream https://alice:{UserInfoSecret}@idp.example/v1",
            new HttpRequestException($"upstream rejected token: {InnerSecret}"));
    }

    [Test]
    public async Task Error_reports_reach_glitchtip_without_credentials_or_personal_data()
    {
        await using var collector = await FakeGlitchTip.StartAsync();
        await using var server = await AuthTestServer.StartAsync(
            settings: values => values["sentry-dsn"] = collector.Dsn,
            routes: app => app.MapPost("/__test/crash", Crash));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/__test/crash?code={QuerySecret}&state=abc")
        {
            Content = new FormUrlEncodedContent([new("password", FormSecret)]),
        };
        request.Headers.Authorization = new("Bearer", BearerSecret);
        request.Headers.Add("Cookie", $"tsauth_session={CookieSecret}");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        using var response = await server.Client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        var envelope = await collector.WaitForEventAsync(TimeSpan.FromSeconds(20));
        // Enough to debug: exception types, stack frames and the route.
        await Assert.That(envelope).Contains("System.InvalidOperationException");
        await Assert.That(envelope).Contains("System.Net.Http.HttpRequestException");
        await Assert.That(envelope).Contains("ErrorReportingTests.Crash()");
        await Assert.That(envelope).Contains("/__test/crash");
        foreach (var secret in AllSecrets)
        {
            await Assert.That(envelope).DoesNotContain(secret);
        }
    }

    [Test]
    public async Task Logs_never_contain_exception_secrets()
    {
        var logs = new CapturingLogs();
        await using var server = await AuthTestServer.StartAsync(
            services: services => services.AddSingleton<ILoggerProvider>(logs),
            routes: app => app.MapPost("/__test/crash", Crash));

        using var response = await server.Client.PostAsync($"/__test/crash?code={QuerySecret}", new FormUrlEncodedContent([new("password", FormSecret)]));
        var text = logs.Text;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(text).Contains("System.InvalidOperationException (message withheld)");
        await Assert.That(text).Contains("System.Net.Http.HttpRequestException (message withheld)");
        await Assert.That(text).Contains("ErrorReportingTests.Crash()");
        foreach (var secret in AllSecrets)
        {
            await Assert.That(text).DoesNotContain(secret);
        }
    }

    [Test]
    public async Task Exceptions_logged_as_values_scopes_or_aggregates_keep_their_secrets_out()
    {
        const string secret = "structured-secret-4d0e9a";
        var logs = new CapturingLogs();
        await using var server = await AuthTestServer.StartAsync(
            services: services => services.AddSingleton<ILoggerProvider>(logs),
            routes: app => app.MapGet("/__test/structured", (ILoggerFactory factory) =>
            {
                var logger = factory.CreateLogger("StructuredTest");
                var failure = new InvalidOperationException($"password={secret}");
                var aggregate = new AggregateException(
                    new IOException($"first {secret}"),
                    new AggregateException(new TimeoutException($"nested {secret}")));
#pragma warning disable CA1848, CA2254 // The point is to log the way arbitrary library code does.
                using (logger.BeginScope(failure))
                using (logger.BeginScope("Scope {Failure}", failure))
                using (logger.BeginScope(new Dictionary<string, object?> { ["Failure"] = failure }))
                {
                    logger.ZLogError($"Failure: {failure:@Error}");
                    var errors = new Exception[] { failure, aggregate };
                    logger.ZLogError($"Failures: {errors:@Errors}");
                    logger.ZLogError(aggregate, $"Aggregate failure");
                    logger.Log(LogLevel.Error, default, new Dictionary<string, object?> { ["Error"] = failure }, null, (state, _) => string.Join(",", state.Values));
                    var nested = new Dictionary<string, object?> { ["inner"] = new List<object?> { failure } };
                    logger.ZLogError($"Nested: {nested:@Context}");
                    var lazy = new[] { failure }.Where(_ => true);
                    logger.ZLogError($"Lazy: {lazy:@Errors}");
                    logger.Log(LogLevel.Error, default, new { Failure = failure }, null, (state, _) => state.ToString()!);
                    logger.BeginScope(new { Failure = failure })?.Dispose();
                    var loop = new AggregateException(failure, failure);
                    for (var i = 0; i < 20; i++)
                    {
                        loop = new AggregateException(loop, loop);
                    }

                    logger.ZLogError(loop, $"Self-similar aggregate");
                    var wrapped = new { Failure = failure };
                    logger.ZLogError($"Wrapped: {wrapped:@Context}");
                    var items = new List<object> { new { Failure = failure } };
                    logger.ZLogError($"Wrapped in list: {items:@Items}");
                    var map = new Dictionary<Exception, string> { [failure] = "value" };
                    logger.ZLogError($"Exception keys: {map:@Map}");
                    var wide = new AggregateException(Enumerable.Range(0, 50_000).Select(i => new IOException($"wide {secret} {i}")));
                    logger.ZLogError(wide, $"Wide aggregate");
                    logger.ZLogError(new AggregateException(Enumerable.Repeat<Exception>(failure, 200_000)), $"Repeated child");
                    var rendering = new SecretRenderingList(secret) { "harmless" };
                    logger.ZLogError($"Rendering collection: {rendering:@Items}");
                    logger.ZLogError($"Endpoint subclass: {new SecretEndPoint(secret):@Peer}");
                    logger.Log(LogLevel.Error, default, new SecretRenderingState(secret), null, (state, _) => state.ToString());
                    logger.BeginScope(new SecretRenderingState(secret))?.Dispose();
                    var level = new List<object?> { "leaf" };
                    for (var i = 0; i < 4; i++)
                    {
                        level = [.. Enumerable.Repeat<object?>(level, 100)];
                    }

                    logger.ZLogError($"Fan-out: {level:@Tree}");
                    logger.ZLogError($"Throwing collection: {new ThrowingCollection():@Items}");
                    logger.BeginScope(new ThrowingCollection())?.Dispose();
                    // Kept on the legacy MEL path: framework code still logs these templates through SafeLogging.
                    logger.LogError("Formatted {Elapsed:F2} ms, {{literal}}, {Missing}", 1.23456, null);
                    logger.ZLogError(new LyingStackTraceException(secret), $"Lying stack trace");
                }
#pragma warning restore CA1848, CA2254
                return Results.Ok();
            }));

        using var response = await server.Client.GetAsync("/__test/structured");
        var text = logs.Text;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(text).Contains("Failure: System.InvalidOperationException (message withheld)");
        // Every child of every aggregate is rendered, without messages.
        await Assert.That(text).Contains("System.IO.IOException (message withheld)");
        await Assert.That(text).Contains("System.TimeoutException (message withheld)");
        await Assert.That(text).DoesNotContain(secret);
        await Assert.That(text).Contains("log state withheld");
        // Self-similar and very wide aggregate graphs both render in bounded size.
        await Assert.That(text.Length).IsLessThan(500_000);
        await Assert.That(text).Contains("further causes truncated");
        await Assert.That(text).Contains("withheld]");
        // Standard template semantics survive normalization.
        await Assert.That(text).Contains("Formatted 1.23 ms, {literal}, (null)");
    }

    [Test]
    public async Task ZLogger_entries_keep_values_and_exception_messages_out_of_every_provider()
    {
        const string secret = LogSecrets.FakeSecret;
        var logs = new FieldLogs();
        using var services = new ServiceCollection().AddLogging(builder => builder.AddProvider(logs)).AddSafeLogging().BuildServiceProvider();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ZLoggerTest");
        var user = "alice";

        logger.ZLogError(new InvalidOperationException($"password={secret}"), $"Sign-in failed for {user:@User}");
        logger.ZLogInformation($"Upstream said {new LogSecrets.UpstreamReply(secret):@Reply}");
        logger.ZLogInformation($"Took {1.5:@Elapsed:0.00} s");

        var text = logs.Text;
        await Assert.That(text).DoesNotContain(secret);
        await Assert.That(text).Contains("Sign-in failed for alice");
        await Assert.That(text).Contains("Took 1.50 s");
        await Assert.That(text).Contains("{OriginalFormat}=Sign-in failed for {User}");
        await Assert.That(text).Contains("User=alice");
        await Assert.That(text).Contains("System.InvalidOperationException (message withheld)");
        await Assert.That(text).Contains("Upstream said [UpstreamReply withheld]");
    }

    [Test]
    public async Task Templates_pad_and_format_like_string_Format()
    {
        var logs = new FieldLogs();
        using var services = new ServiceCollection().AddLogging(builder => builder.AddProvider(logs)).AddSafeLogging().BuildServiceProvider();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("FormatTest");

        // LogValuesFormatter rejects ZLogger's comma formats, so the state carries the template the way ZLogger writes it.
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Name", "ab"),
            new("Count", 1.5),
            new("Length", 42),
            new("Name", "cd"),
            new("{OriginalFormat}", "Padded [{Name,-10:0.00}] {Count,0.00} {Length,5} {Name,5}"),
        };
#pragma warning disable CA1848 // The point is to log the way arbitrary library code does.
        logger.Log(LogLevel.Information, default, state, null, (value, _) => value.ToString()!);
#pragma warning restore CA1848

        var text = logs.Text;
        await Assert.That(text).Contains(string.Format(CultureInfo.InvariantCulture, "Padded [{0,-10:0.00}] {1:0.00} {2,5} {3,5}", "ab", 1.5, 42, "cd"));
    }

    [Test]
    public async Task A_failing_log_provider_never_stops_the_error_envelope()
    {
        await using var server = await AuthTestServer.StartAsync(
            services: services => services.AddSingleton<ILoggerProvider>(new ThrowingLogs()),
            routes: app => app.MapPost("/__test/crash", Crash));

        using var response = await server.Client.PostAsync("/__test/crash", null);
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(body).Contains("internal_error");
    }

    [Test]
    public async Task An_envelope_that_cannot_be_written_aborts_without_an_unhandled_error()
    {
        var logs = new CapturingLogs();
        await using var server = await AuthTestServer.StartAsync(
            services: services => services.AddSingleton<ILoggerProvider>(logs),
            routes: app => app.MapGet("/__test/broken-body", (HttpContext context) =>
            {
                context.Response.Body = new BrokenStream();
                throw new InvalidOperationException("forces an envelope write");
            }));

        HttpRequestException? failure = null;
        try
        {
            using var response = await server.Client.GetAsync("/__test/broken-body");
            await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException exception)
        {
            failure = exception;
        }

        using var healthy = await server.Client.GetAsync("/healthz");
        await Assert.That(failure).IsNotNull();
        await Assert.That(healthy.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(logs.Text).DoesNotContain("An unhandled exception was thrown by the application");
    }

    /// <summary>An endpoint whose rendering leaks a secret, though IPEndPoint itself is safe.</summary>
    private sealed class SecretEndPoint(string secret) : IPEndPoint(IPAddress.Loopback, 1)
    {
        public override string ToString() => $"password={secret}";
    }

    /// <summary>Structured state with only harmless values whose own rendering leaks a secret.</summary>
    private sealed class SecretRenderingState(string secret) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] values = [new("Harmless", "value")];

        public int Count => this.values.Length;

        public KeyValuePair<string, object?> this[int index] => this.values[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)this.values).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();

        public override string ToString() => $"password={secret}";
    }

    /// <summary>Plants secrets in frame variables, context lines and the mechanism, which an SDK
    /// integration could populate, so the scrubber is tested on them.</summary>
    private sealed class FrameSecretPlanter : Sentry.Extensibility.ISentryEventProcessor
    {
        public Sentry.SentryEvent? Process(Sentry.SentryEvent @event)
        {
            @event.AddBreadcrumb($"attached directly {FrameSecret}");
            @event.SetExtra($"https://idp.example/callback?code={FrameSecret}", "failed");
            @event.DebugImages ??= [];
            @event.DebugImages.Add(new Sentry.Protocol.DebugImage { Type = "pe_dotnet", CodeFile = $"/home/{FrameSecret}/app.dll" });
            foreach (var exception in @event.SentryExceptions ?? [])
            {
                if (exception.Mechanism is { } mechanism)
                {
                    mechanism.Description = $"mechanism {FrameSecret}";
                    // Plausible-looking alphanumeric names must not pass either.
                    mechanism.Type = AlphanumericSecret;
                    mechanism.Source = $"source {FrameSecret}";
                }

                foreach (var frame in exception.Stacktrace?.Frames ?? [])
                {
                    frame.Vars["password"] = FrameSecret;
                    frame.ContextLine = $"var token = \"{FrameSecret}\";";
                    frame.PreContext.Add(FrameSecret);
                }
            }

            return @event;
        }
    }

    /// <summary>An exception whose virtual StackTrace returns a secret instead of frames.</summary>
    private sealed class LyingStackTraceException(string secret) : Exception("message")
    {
        public override string StackTrace => $"password={secret}";
    }

    /// <summary>A collection that throws as soon as anything enumerates it.</summary>
    private sealed class ThrowingCollection : System.Collections.ICollection, IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => 1;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public KeyValuePair<string, object?> this[int index] => throw new InvalidOperationException("enumeration failure");

        public void CopyTo(Array array, int index) => throw new InvalidOperationException("enumeration failure");

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw new InvalidOperationException("enumeration failure");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new InvalidOperationException("enumeration failure");
    }

    /// <summary>A collection of harmless strings whose own rendering leaks a secret.</summary>
    private sealed class SecretRenderingList(string secret) : System.Collections.ArrayList
    {
        public override string ToString() => $"password={secret}";
    }

    private sealed class ThrowingLogs : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger();

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                {
                    throw new InvalidOperationException("log provider failure");
                }
            }
        }
    }

    /// <summary>Records each provider's view of an entry: the formatted line, the exception rendering and the structured fields.</summary>
    private sealed class FieldLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();

        public string Text => string.Join('\n', this.lines);

        public ILogger CreateLogger(string categoryName) => new Logger(this.lines);

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
                var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(", ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                    : state?.GetType().FullName;
                lines.Enqueue($"{formatter(state, exception)} {exception} [{fields}]");
            }
        }
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }

        public override void Flush() => throw new IOException("broken transport");

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("broken transport");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("broken transport"));

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new IOException("broken transport"));
    }

    [Test]
    public async Task A_failure_after_the_response_started_aborts_the_connection_and_logs_safely()
    {
        var logs = new CapturingLogs();
        await using var server = await AuthTestServer.StartAsync(
            services: services => services.AddSingleton<ILoggerProvider>(logs),
            routes: app => app.MapGet("/__test/stream", async (HttpContext context) =>
            {
                await context.Response.WriteAsync("partial ");
                await context.Response.Body.FlushAsync();
                throw new InvalidOperationException($"stream failed with token={StreamSecret}");
            }));

        HttpRequestException? failure = null;
        try
        {
            using var response = await server.Client.GetAsync("/__test/stream");
            await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(logs.Text).Contains("Request failed after the response started");
        await Assert.That(logs.Text).DoesNotContain(StreamSecret);
    }

    /// <summary>A loopback stand-in for GlitchTip's Sentry-compatible envelope endpoint.</summary>
    private sealed class FakeGlitchTip : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly ConcurrentQueue<string> received;

        private FakeGlitchTip(WebApplication app, ConcurrentQueue<string> received, string dsn)
        {
            this.app = app;
            this.received = received;
            this.Dsn = dsn;
        }

        public string Dsn { get; }

        public static async Task<FakeGlitchTip> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var received = new ConcurrentQueue<string>();
            app.MapPost("/api/{project}/envelope/", async (HttpContext context) =>
            {
                Stream body = context.Request.Body;
                if (context.Request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
                {
                    body = new GZipStream(body, CompressionMode.Decompress);
                }

                using var reader = new StreamReader(body, Encoding.UTF8);
                received.Enqueue(await reader.ReadToEndAsync());
                return Results.Ok();
            });
            await app.StartAsync();
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return new FakeGlitchTip(app, received, $"http://publickey@127.0.0.1:{address.Port}/1");
        }

        public async Task<string> WaitForEventAsync(TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            var others = 0;
            while (DateTimeOffset.UtcNow < deadline)
            {
                while (this.received.TryDequeue(out var envelope))
                {
                    others++;
                    if (envelope.Contains("\"type\":\"event\"", StringComparison.Ordinal))
                    {
                        return envelope;
                    }
                }

                await Task.Delay(100);
            }

            throw new TimeoutException($"No error event reached the collector ({others} other envelopes).");
        }

        public async ValueTask DisposeAsync()
        {
            await this.app.StopAsync();
            await this.app.DisposeAsync();
        }
    }
}
