using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraBase.Slow.Replication;

internal sealed class FaultSoakFeed : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly ConcurrentDictionary<(string Id, string Operation), string> _seen = new();
    private readonly HashSet<(string Origin, ulong Version, string Collection, string Id)> _events = [];
    private readonly Dictionary<string, ulong> _versions = [with(StringComparer.Ordinal)];
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _run;
    private ExceptionDispatchInfo? _failure;
    private string? _cursor;
    private int _resumedConnections;

    private FaultSoakFeed(Func<HttpClient> client, string changesPath, CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _run = RunAsync(client, changesPath, _stop.Token);
    }

    public static async Task<FaultSoakFeed> StartAsync(Func<HttpClient> client, string changesPath, CancellationToken cancellationToken)
    {
        var feed = new FaultSoakFeed(client, changesPath, cancellationToken);
        try
        {
            await feed._ready.Task.WaitAsync(cancellationToken);
            return feed;
        }
        catch
        {
            await feed.DisposeAsync();
            throw;
        }
    }

    public bool Has(string id, string operation, JsonNode? document)
    {
        ThrowIfFailed();
        return _seen.TryGetValue((id, operation == "put" ? "upsert" : operation), out var body)
            && (operation == "put" && document is null || body == FaultSoakChecks.Canonical(document));
    }

    public void ThrowIfFailed() => Volatile.Read(ref _failure)?.Throw();

    public int ResumedConnections => Volatile.Read(ref _resumedConnections);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _run;
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private async Task RunAsync(Func<HttpClient> client, string changesPath, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // The factory pins the region and returns its current borrowed client after a process restart.
                    var current = client();
                    using var request = new HttpRequestMessage(HttpMethod.Get, changesPath)
                    {
                        Version = current.DefaultRequestVersion,
                        VersionPolicy = current.DefaultVersionPolicy,
                    };
                    request.Headers.Accept.ParseAdd("text/event-stream");
                    if (_cursor is not null)
                    {
                        request.Headers.Add("Last-Event-ID", _cursor);
                    }

                    using var response = await current.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                    {
                        throw new IOException("The feed region is temporarily unavailable.");
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"The changes feed returned HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
                    }

                    if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                    {
                        throw new InvalidOperationException("The changes feed did not return an event stream.");
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream);
                    await ReadAsync(reader, request.Headers.Contains("Last-Event-ID"), cancellationToken);
                }
                catch (Exception error) when (error is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
                {
                    // A killed process closes the stream; the last complete event remains the resume position.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = _ready.TrySetCanceled(cancellationToken);
        }
        catch (Exception error)
        {
            Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(error));
            _ = _ready.TrySetException(error);
            throw;
        }
    }

    private async Task ReadAsync(StreamReader reader, bool resumed, CancellationToken cancellationToken)
    {
        string? type = null;
        string? cursor = null;
        string? data = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length > 1024 * 1024)
            {
                throw new InvalidOperationException("The soak feed event exceeded 1 MiB.");
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                type = line[7..];
            }
            else if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                cursor = line[4..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                if (data is not null)
                {
                    throw new InvalidOperationException("The soak feed unexpectedly split its JSON across data lines.");
                }

                data = line[6..];
            }
            else if (line.Length == 0)
            {
                if (type == "error")
                {
                    throw new InvalidOperationException("The changes feed emitted a terminal error: " + data);
                }

                if (type == "change")
                {
                    using var json = JsonDocument.Parse(data ?? throw new InvalidOperationException("A feed change has no data."));
                    var id = json.RootElement.GetProperty("id").GetString() ?? throw new InvalidOperationException("A feed change has no document ID.");
                    var operation = json.RootElement.GetProperty("op").GetString() ?? throw new InvalidOperationException("A feed change has no operation.");
                    var body = FaultSoakChecks.Canonical(JsonNode.Parse(json.RootElement.GetProperty("document").GetRawText()));
                    var origin = json.RootElement.GetProperty("origin").GetString()!;
                    var version = ulong.Parse(json.RootElement.GetProperty("version").GetString()!, CultureInfo.InvariantCulture);
                    var collection = json.RootElement.GetProperty("collection").GetString()!;
                    _seen[(id, operation)] = body;
                    if (_events.Add((origin, version, collection, id)))
                    {
                        if (_versions.TryGetValue(origin, out var previous) && version < previous)
                        {
                            throw new InvalidOperationException($"The feed reordered origin {origin} at {id}.");
                        }

                        _versions[origin] = version;
                    }
                    // Feed bodies are current values; a replay after deletion may carry null.
                    if (_events.Count > 1000)
                    {
                        throw new InvalidOperationException("The soak feed exceeded its 1000-operation observation limit.");
                    }
                }

                if (cursor is not null)
                {
                    _cursor = cursor;
                    if (resumed)
                    {
                        _ = Interlocked.Increment(ref _resumedConnections);
                        resumed = false;
                    }

                    _ = _ready.TrySetResult();
                }

                (type, cursor, data) = (null, null, null);
            }
        }
    }
}
