using TerraScale.Endpoints;

namespace TerraScale.Endpoints.Example;

public sealed record CounterSignals(int Clicks);

public sealed record EchoSignals(string Input);

/// <summary>Signals roundtrip: Datastar POSTs the signals as JSON; the endpoint patches them back.</summary>
public sealed class DatastarCounterEndpoint : Endpoint<CounterSignals, IResult>
{
    public override void Configure() { Post("/datastar/counter"); ProducesSse(); }

    public override Task<IResult> HandleAsync(CounterSignals request, CancellationToken cancellationToken) =>
        Task.FromResult(Datastar.Stream(
            (datastar, ct) => datastar.PatchSignalsAsync(new { clicks = request.Clicks + 1 }, cancellationToken: ct)));
}

/// <summary>GET signals: Datastar sends the signals in the datastar query parameter; ReadSignalsAsync decodes them.</summary>
public sealed class DatastarEchoEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure() { Get("/datastar/echo"); ProducesSse(); }

    public override async Task<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var signals = await ReadSignalsAsync<EchoSignals>(cancellationToken);
        return Datastar.Stream(
            (datastar, ct) => datastar.PatchSignalsAsync(new { output = $"echo: {signals.Input}" }, cancellationToken: ct));
    }
}

/// <summary>Live stream: patches the clock element every second until the browser disconnects.</summary>
public sealed class DatastarClockEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure() { Get("/datastar/clock"); ProducesSse(); }

    public override Task<IResult> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Datastar.Stream(async (datastar, ct) =>
        {
            while (!ct.IsCancellationRequested)
            {
                await datastar.PatchElementsAsync(
                    $"<time id=\"clock\">{DateTimeOffset.Now:HH:mm:ss}</time>",
                    cancellationToken: ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }));
}
