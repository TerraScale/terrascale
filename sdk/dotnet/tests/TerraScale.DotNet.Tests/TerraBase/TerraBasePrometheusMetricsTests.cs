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
