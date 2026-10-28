using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Fast.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;
using TerraScale.Endpoints;

namespace TerraBase.Fast.Management;

/// <summary>
/// Bytes a handler leaves in the response writer without flushing, over real HTTP: an error before the first flush, a client
/// that aborts, and bytes a later flush, stream write or start hands to the server. Each minute meters the bytes the
/// client could receive in it, and only those.
/// </summary>
public sealed class UsageWriterTests
{
    private const int Pending = 1000;
    private const int Flushed = 2000;

    [Test]
    public async Task An_error_before_the_first_flush_answers_only_the_error_and_meters_it()
    {
        await using var probe = await Probe.StartAsync();
        string outcome;
        try
        {
            using var response = await probe.Api.Client.GetAsync(Probe.Route("throw"));
            outcome = $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
        }
        catch (HttpRequestException error)
        {
            outcome = $"{error.GetType().Name}: {error.Message}";
        }

        await probe.CompletedAsync();
        var error500 = JsonSerializer.Serialize(new ErrorBody(new ErrorDetail("internal_error")), TerraBaseJsonContext.Default.ErrorBody);
        var sent = await probe.SentAsync();
        Console.WriteLine($"throw: {probe.State} outcome={outcome} sent=[{string.Join(',', sent)}]");
        // The writer's private bytes neither precede the error document nor are metered.
        await Assert.That(outcome).IsEqualTo($"500 {error500}");
        await Assert.That(sent).IsEquivalentTo([(long)Encoding.UTF8.GetByteCount(error500), 0L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_client_that_aborts_before_a_flush_is_metered_none_of_the_unflushed_bytes()
    {
        await using var probe = await Probe.StartAsync();
        using var abort = new CancellationTokenSource();
        var request = probe.Api.Client.GetAsync(Probe.Route("abort"), HttpCompletionOption.ResponseHeadersRead, abort.Token);
        await probe.State.Reached.Task;
        await abort.CancelAsync();
        await Assert.That(async () => await request).Throws<OperationCanceledException>();

        await probe.CompletedAsync();
        var sent = await probe.SentAsync();
        Console.WriteLine($"abort: {probe.State} sent=[{string.Join(',', sent)}]");
        await Assert.That(sent).IsEquivalentTo([0L, 0L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Writer_bytes_are_metered_in_the_minute_they_are_flushed_and_an_unflushed_tail_in_the_minute_it_is_sent()
    {
        await using var probe = await Probe.StartAsync();
        using var response = await probe.Api.Client.GetAsync(Probe.Route("minutes"), HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();
        await probe.State.Reached.Task;
        var first = await ReadAsync(body, Flushed);
        probe.ReportActive();
        var open = await probe.SentAsync();

        probe.Clock.Advance(TimeSpan.FromMinutes(1));
        probe.State.Resume.SetResult();
        var rest = await ReadAsync(body, int.MaxValue);
        await probe.CompletedAsync();
        var sent = await probe.SentAsync();
        Console.WriteLine($"minutes: {probe.State} read=[{first},{rest}] open=[{string.Join(',', open)}] sent=[{string.Join(',', sent)}]");
        await Assert.That(open).IsEquivalentTo([first, 0L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(sent).IsEquivalentTo([first, rest], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Writer_bytes_a_stream_write_sends_are_metered_in_its_minute_and_while_the_request_stays_open()
    {
        await using var probe = await Probe.StartAsync();
        using var response = await probe.Api.Client.GetAsync(Probe.Route("stream"), HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();
        await probe.State.Reached.Task;
        // Whatever the stream's flushed bytes follow on the connection was sent no later than they were.
        var first = await ReadAsync(body, Flushed, (byte)'s');
        probe.ReportActive();
        var open = await probe.SentAsync();

        probe.Clock.Advance(TimeSpan.FromMinutes(1));
        probe.State.Resume.SetResult();
        var rest = await ReadAsync(body, int.MaxValue);
        await probe.CompletedAsync();
        var sent = await probe.SentAsync();
        Console.WriteLine($"stream: {probe.State} read=[{first},{rest}] open=[{string.Join(',', open)}] sent=[{string.Join(',', sent)}]");
        await Assert.That(open).IsEquivalentTo([first, 0L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(sent).IsEquivalentTo([first, rest], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Writer_bytes_starting_the_response_sends_are_metered_in_its_minute()
    {
        await using var probe = await Probe.StartAsync();
        var request = probe.Api.Client.GetAsync(Probe.Route("start"));
        await probe.State.Reached.Task;
        // By the server's own count of the writer's unsent bytes, which grows by the headers when it starts without sending.
        var started = probe.State.StartSent;
        await Assert.That(started).IsBetween(0L, Pending);
        probe.ReportActive();
        var open = await probe.SentAsync();

        probe.Clock.Advance(TimeSpan.FromMinutes(1));
        probe.State.Resume.SetResult();
        using var response = await request;
        var read = (await response.Content.ReadAsByteArrayAsync()).LongLength;
        await probe.CompletedAsync();
        var sent = await probe.SentAsync();
        Console.WriteLine($"start: {probe.State} read={read} open=[{string.Join(',', open)}] sent=[{string.Join(',', sent)}]");
        await Assert.That(open).IsEquivalentTo([started, 0L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(sent).IsEquivalentTo([started, read - started], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // Reads until `count` bytes (of `value` only, when given) arrived, or to the end; returns every byte read.
    private static async Task<long> ReadAsync(Stream body, int count, byte? value = null)
    {
        var buffer = new byte[4096];
        long read = 0, matched = 0;
        while (matched < count)
        {
            var length = await body.ReadAsync(buffer);
            if (length == 0)
            {
                return count == int.MaxValue ? read : throw new IOException($"the body ended after {read} bytes");
            }

            read += length;
            matched += value is { } only ? buffer.AsSpan(0, length).Count(only) : length;
        }

        return read;
    }

    // A server with the probe endpoint, attributing its requests to a database no other request is attributed to.
    private sealed class Probe : IAsyncDisposable
    {
        private long _minute;
        private string _region = "";

        public required DocumentApi Api { get; init; }
        public required FakeTimeProvider Clock { get; init; }
        public required ProbeState State { get; init; }

        public static string Route(string mode) => $"/v1/usage-writer-probe/{mode}";

        public static async Task<Probe> StartAsync()
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var state = new ProbeState();
            var api = await DocumentApi.StartAsync(time: clock, services: services =>
            {
                services.AddSingleton(state);
                services.AddTerraScaleEndpoints([EndpointRegistration.Create<UsageWriterProbeEndpoint>()]);
            });
            (await api.SendAsync(HttpMethod.Put, api.DatabasePath("probed"))).Status.ShouldBe(HttpStatusCode.Created);
            state.Database = await api.TenantOfAsync("probed");
            return new Probe { Api = api, Clock = clock, State = state }.At(clock.GetUtcNow().ToUnixTimeSeconds() / 60,
                api.Server.Services.GetRequiredService<TerraBaseOptions>().Region);
        }

        public void ReportActive() => Api.Server.Services.GetRequiredService<UsageMeter>().ReportActive();

        // Client bytes sent in the probe's first minute and the next.
        public async Task<long[]> SentAsync() => [.. (await SeriesAsync()).Select(point => Meter(point, "client_bytes", "sent"))];

        // The probe's one request was recorded.
        public Task CompletedAsync() => TerraBaseTestServer.EventuallyAsync(
            async _ => (await SeriesAsync()).Sum(point => Meter(point, "requests", "get")) == 1, TimeSpan.FromSeconds(30));

        public ValueTask DisposeAsync()
        {
            State.Resume.TrySetResult();
            return Api.DisposeAsync();
        }

        private Probe At(long minute, string region)
        {
            (_minute, _region) = (minute, region);
            return this;
        }

        private long Meter(OpCounts point, string meter, string kind) =>
            point.Meters?.GetValueOrDefault(new UsageDimension(meter, _region, "", kind).Key)?.Count ?? 0;

        private async Task<OpCounts[]> SeriesAsync() =>
            await Api.Server.Services.GetRequiredService<UsageMeter>()
                .GetObservationsAsync(State.Database!, new UsageWindow(UsageWindow.Minute, _minute, 2), CancellationToken.None) switch
            {
                UsageCounts counts => counts.Series,
                Unavailable error => throw new InvalidOperationException(error.Message),
            };
    }

    private sealed class ProbeState
    {
        public TenantContext? Database { get; set; }
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started { get; private set; }
        public long Unflushed { get; private set; } = -1;
        public long StartSent { get; set; } = -1;

        public void Note(HttpResponse response)
        {
            Started = response.HasStarted;
            Unflushed = response.BodyWriter.CanGetUnflushedBytes ? response.BodyWriter.UnflushedBytes : -1;
        }

        public override string ToString() => $"started={Started} unflushed={Unflushed} startSent={StartSent}";
    }

    /// <summary>Test-only writer of the probe's response bodies; no production route is registered by this suite.</summary>
    public sealed class UsageWriterProbeEndpoint : EndpointWithoutRequest<IResult>
    {
        /// <inheritdoc />
        public override void Configure()
        {
            Get("/v1/usage-writer-probe/{mode}");
            Name("usage-writer-probe");
            AllowAnonymous();
            Metadata(ListenerMetadata.Client);
        }

        /// <inheritdoc />
        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var state = HttpContext.RequestServices.GetRequiredService<ProbeState>();
            UsageRequestMetering.Attribute(HttpContext, state.Database!);
            var response = HttpContext.Response;
            var writer = response.BodyWriter;
            switch (HttpContext.Request.RouteValues["mode"])
            {
                case "throw":
                    Fill(writer, Pending);
                    state.Note(response);
                    throw new InvalidOperationException("the probe fails before its first flush");
                case "abort":
                    Fill(writer, Pending);
                    state.Note(response);
                    state.Reached.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    break;
                case "minutes":
                    Fill(writer, Flushed);
                    await writer.FlushAsync(CancellationToken.None);
                    state.Note(response);
                    state.Reached.SetResult();
                    await state.Resume.Task;
                    Fill(writer, Flushed);
                    await writer.FlushAsync(CancellationToken.None);
                    Fill(writer, Pending);
                    break;
                case "stream":
                    Fill(writer, Pending);
                    await response.Body.WriteAsync(Enumerable.Repeat((byte)'s', Flushed).ToArray(), CancellationToken.None);
                    await response.Body.FlushAsync(CancellationToken.None);
                    state.Note(response);
                    state.Reached.SetResult();
                    await state.Resume.Task;
                    break;
                case "start":
                    Fill(writer, Pending);
                    var unsent = writer.UnflushedBytes;
                    await response.StartAsync(CancellationToken.None);
                    state.Note(response);
                    state.StartSent = Math.Max(0, unsent - state.Unflushed);
                    state.Reached.SetResult();
                    await state.Resume.Task;
                    break;
            }

            return TypedResults.Empty;
        }

        private static void Fill(PipeWriter writer, int count)
        {
            writer.GetSpan(count)[..count].Fill((byte)'w');
            writer.Advance(count);
        }
    }
}
