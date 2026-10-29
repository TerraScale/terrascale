using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBasePrometheusMetricsTests
{
    private const string Media = "text/plain; version=0.0.4; charset=utf-8";

    [Test]
    public async Task Contract_route_token_and_exposition_are_preserved_without_JSON_decoding()
    {
        using var contract = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("project-metrics.json"));
        var fixture = contract.RootElement;
        var bytes = Encoding.UTF8.GetBytes(fixture.GetProperty("body").GetString()!);
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (request, _) =>
        {
            calls++;
            await Assert.That(request.Method.Method).IsEqualTo(fixture.GetProperty("method").GetString());
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(fixture.GetProperty("path").GetString()!
                .Replace("{project}", TerraBaseIntegrationFixture.ProjectId, StringComparison.Ordinal));
            await Assert.That(request.RequestUri.Query).IsEqualTo("");
            await Assert.That(request.Content).IsNull();
            await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
            await Assert.That(request.Headers.Accept.ToString()).IsEqualTo(fixture.GetProperty("mediaType").GetString());
            await Assert.That(request.Headers.GetValues("X-TerraScale-Region-Pin").Single()).IsEqualTo("sjc");
            return Exposition(bytes);
        });
        using var http = new HttpClient(handler);
        var project = TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId);
        var result = await project.GetPrometheusMetricsAsync();
        await Assert.That(result.Body.Span.SequenceEqual(bytes)).IsTrue();
        await Assert.That(result.ContentType).IsEqualTo(Media);
        await Assert.That(calls).IsEqualTo(1);
        // The SDK has disposed only its response, so the same caller-owned HttpClient remains usable.
        await project.GetPrometheusMetricsAsync();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    [Arguments(400, "invalid_request")]
    [Arguments(401, "invalid_key")]
    [Arguments(403, "forbidden_scope")]
    [Arguments(404, "unknown_database")]
    [Arguments(421, "wrong_region")]
    [Arguments(503, "capacity")]
    public async Task JSON_errors_keep_status_code_headers_and_never_replay(int status, string code)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            var response = TerraBaseIntegrationFixture.Json($$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""", (HttpStatusCode)status);
            response.Headers.Add("X-Request-ID", TerraBaseIntegrationFixture.ProjectId);
            response.Headers.Add("X-TerraBase-Region", "gru");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var project = TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => project.GetPrometheusMetricsAsync());
        await Assert.That(error!.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(error.ErrorCode).IsEqualTo(code);
        await Assert.That(error.RequestId).IsEqualTo(TerraBaseIntegrationFixture.ProjectId);
        await Assert.That(error.Region).IsEqualTo("gru");
        await Assert.That(error.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(null)]
    [Arguments("application/json")]
    [Arguments("text/plain; version=1.0; charset=utf-8")]
    [Arguments("text/plain; version=0.0.4; charset=utf-16")]
    public async Task Unexpected_media_is_not_treated_as_a_scrape(string? media)
    {
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Exposition([], media)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => TerraBaseIntegrationFixture.Client(http)
            .Project(TerraBaseIntegrationFixture.ProjectId).GetPrometheusMetricsAsync());
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    [Arguments(204)]
    [Arguments(206)]
    [Arguments(302)]
    public async Task Partial_success_and_redirects_are_not_followed_or_returned(int status)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            var response = Exposition([]);
            response.StatusCode = (HttpStatusCode)status;
            response.Headers.Location = new Uri("https://other.example.test/");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => TerraBaseIntegrationFixture.Client(http)
            .Project(TerraBaseIntegrationFixture.ProjectId).GetPrometheusMetricsAsync());
        await Assert.That(error!.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Fixed_body_bound_rejects_advertised_and_underreported_sizes(bool underreported)
    {
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            var response = Exposition(new byte[8 * 1024 * 1024 + 1]);
            if (underreported) response.Content.Headers.ContentLength = 0;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => TerraBaseIntegrationFixture.Client(http)
            .Project(TerraBaseIntegrationFixture.ProjectId).GetPrometheusMetricsAsync());
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Caller_cancellation_and_fixed_deadline_cover_body_reads_and_dispose_response(bool expire)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var body = new WaitingBody(started);
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(Media);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var clock = new DeadlineClock();
        var connection = new TerraBaseConnection(http, new Uri("https://api.example.test"), null, "project-token", "token",
            TimeSpan.FromMinutes(2), TerraBaseConnection.DefaultMaxResponseBytes, timeProvider: clock);
        var project = new TerraBaseProjectClient(connection, "/v1/projects/" + TerraBaseIntegrationFixture.ProjectId);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => project.GetPrometheusMetricsAsync(canceled.Token));
        await Assert.That(calls).IsEqualTo(0);
        using var cancel = new CancellationTokenSource();
        var pending = project.GetPrometheusMetricsAsync(cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (expire)
        {
            clock.ExpireMetrics();
            await Assert.ThrowsAsync<TimeoutException>(() => pending);
        }
        else
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        }
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(body.Disposed).IsTrue();
    }

    private static HttpResponseMessage Exposition(byte[] bytes, string? media = Media)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (media is not null) response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(media);
        return response;
    }

    private sealed class WaitingBody(TaskCompletionSource started) : MemoryStream
    {
        public bool Disposed { get; private set; }
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private readonly List<DeadlineTimer> timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DeadlineTimer(callback, state, dueTime);
            timers.Add(timer);
            return timer;
        }
        public void ExpireMetrics() => timers.Last(t => t.DueTime == TimeSpan.FromSeconds(30)).Fire();
        private sealed class DeadlineTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            public TimeSpan DueTime { get; } = dueTime;
            private TimerCallback? pending = callback;
            public void Fire() => Interlocked.Exchange(ref pending, null)?.Invoke(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Interlocked.Exchange(ref pending, null);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
