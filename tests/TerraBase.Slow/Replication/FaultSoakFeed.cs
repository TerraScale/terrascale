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
    private HttpClient? _connected;
    private Dictionary<string, string>? _snapshot;

    private FaultSoakFeed(FaultSoakCluster cluster, string region, Ulid incarnation, CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _run = RunAsync(cluster, region, incarnation, _stop.Token);
    }

    public static async Task<FaultSoakFeed> StartAsync(FaultSoakCluster cluster, string region, CancellationToken cancellationToken)
    {
        var feed = new FaultSoakFeed(cluster, region, await cluster.IncarnationAsync(region, cancellationToken), cancellationToken);
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

    public bool Covers(string id, string operation, JsonNode? document)
    {
        if (Has(id, operation, document))
        {
            return true;
        }

        // A certified replacement preserves state, but cannot promise the old feed history.
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot is not null && (document is null
            ? !snapshot.ContainsKey(id) || Has(id, "delete", null)
            : snapshot.TryGetValue(id, out var body) && body == FaultSoakChecks.Canonical(document));
    }

    public IReadOnlyDictionary<string, string>? Snapshot => Volatile.Read(ref _snapshot);

    public bool ConnectedTo(HttpClient client)
    {
        ThrowIfFailed();
        return ReferenceEquals(Volatile.Read(ref _connected), client);
    }

    public void ThrowIfFailed() => Volatile.Read(ref _failure)?.Throw();

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

    private async Task RunAsync(FaultSoakCluster cluster, string region, Ulid incarnation, CancellationToken cancellationToken)
    {
        var resync = false;
        var changesPath = $"{cluster.DatabasePath}/changes?collections={cluster.Collection}";
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Pin the region and borrow its current client after a process restart.
                    var serving = await cluster.IncarnationAsync(region, cancellationToken);
                    var current = cluster[region];
                    using var request = new HttpRequestMessage(HttpMethod.Get, resync && _cursor is null ? changesPath + "&cursor=now" : changesPath)
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
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (response.StatusCode == HttpStatusCode.Gone && Expired(body))
                        {
                            throw new CursorExpiredException();
                        }

                        throw new InvalidOperationException($"The changes feed returned HTTP {(int)response.StatusCode}: {body}");
                    }

                    if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                    {
                        throw new InvalidOperationException("The changes feed did not return an event stream.");
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream);
                    await ReadAsync(reader, async () =>
                    {
                        if (await cluster.IncarnationAsync(region, cancellationToken) != serving)
                        {
                            throw new CursorExpiredException();
                        }

                        if (resync)
                        {
                            Dictionary<string, string>? snapshot = null;
                            await FaultSoakChecks.UntilAsync(async () =>
                            {
                                snapshot = await FaultSoakChecks.ListAsync(current, cluster.DatabasePath, cluster.Collection, cancellationToken);
                                return snapshot is not null;
                            }, "replacement copy snapshot", cancellationToken);
                            if (await cluster.IncarnationAsync(region, cancellationToken) != incarnation)
                            {
                                throw new CursorExpiredException();
                            }

                            Volatile.Write(ref _snapshot, snapshot);
                            resync = false;
                            Console.WriteLine($"R8 feed {region} snapshot resynced incarnation={incarnation}");
                        }

                        // Bind subsequent expiry to the copy that actually issued this cursor.
                        incarnation = serving;
                        Volatile.Write(ref _connected, current);
                    }, cancellationToken);
                }
                catch (CursorExpiredException)
                {
                    var replacement = await cluster.IncarnationAsync(region, cancellationToken);
                    FaultSoakChecks.Check(replacement != incarnation,
                        $"{region} expired a feed cursor without replacing copy incarnation {incarnation}");
                    Console.WriteLine($"R8 feed {region} cursor_expired: incarnation {incarnation} -> {replacement}");
                    incarnation = replacement;
                    _cursor = null;
                    resync = true;
                    Volatile.Write(ref _snapshot, null);
                }
                catch (Exception error) when (error is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
                {
                    // A killed process closes the stream; the last complete event remains the resume position.
                }
                finally
                {
                    Volatile.Write(ref _connected, null);
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

    private async Task ReadAsync(StreamReader reader, Func<Task> connected, CancellationToken cancellationToken)
    {
        var firstCursor = true;
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
                    if (data is not null && Expired(data))
                    {
                        throw new CursorExpiredException();
                    }

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
                    if (firstCursor)
                    {
                        await connected();
                        firstCursor = false;
                    }

                    _ = _ready.TrySetResult();
                }

                (type, cursor, data) = (null, null, null);
            }
        }
    }

    private static bool Expired(string body) =>
        JsonNode.Parse(body)?["error"]?["code"]?.GetValue<string>() == "cursor_expired";

    private sealed class CursorExpiredException : Exception;
}
