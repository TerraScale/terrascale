using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TerraBase.Testing;

/// <summary>
/// A NATS server holding one JetStream KV bucket, in process over plain TCP: enough of the protocol for TerraBase's member
/// store. Like ts-orchestrator's broker for the terrabase role (<c>internal/roleidentity</c>), a client may publish only to
/// the bucket's subjects, its stream info and its message gets; anything else (a consumer for a key watcher) gets a
/// permissions violation and no answer, as there.
/// </summary>
public sealed class FakeNats : IAsyncDisposable
{
    private const string Bucket = "terrabase_backup_leases";
    private const string Stream = "KV_" + Bucket;
    private const string Subjects = "$KV." + Bucket + ".";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (ulong Sequence, byte[] Headers, byte[] Data)> _last = [with(StringComparer.Ordinal)];
    private readonly Task _serving;
    private ulong _sequence;
    private int _violations;

    public FakeNats()
    {
        _listener.Start();
        _serving = ServeAsync(_stop.Token);
    }

    /// <summary><c>nats://</c> URL of this server.</summary>
    public string Url => $"nats://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Publishes the server refused.</summary>
    public int Violations => Volatile.Read(ref _violations);

    /// <summary>Stores a message under <paramref name="key"/> as a client or the server itself would.</summary>
    public void Store(string key, string? headers, byte[] data)
    {
        lock (_gate)
        {
            _last[Subjects + key] = (++_sequence, headers is null ? [] : Encoding.ASCII.GetBytes(headers), data);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _serving;
        _stop.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        var connections = new List<Task>();
        try
        {
            while (true)
            {
                connections.Add(ConnectionAsync(await _listener.AcceptTcpClientAsync(cancellationToken), cancellationToken));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            await Task.WhenAll(connections);
        }
    }

    private async Task ConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = new BufferedStream(client.GetStream());
            Dictionary<string, string> subscriptions = [with(StringComparer.Ordinal)];
            try
            {
                await WriteAsync(stream, "INFO {\"server_id\":\"fake\",\"server_name\":\"fake\",\"version\":\"2.12.0\",\"proto\":1,\"headers\":true,\"max_payload\":1048576,\"jetstream\":true}\r\n", [], cancellationToken);
                while (await ReadLineAsync(stream, cancellationToken) is { } line)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    switch (parts.ElementAtOrDefault(0)?.ToUpperInvariant())
                    {
                        case "PING":
                            await WriteAsync(stream, "PONG\r\n", [], cancellationToken);
                            break;
                        case "SUB":
                            subscriptions[parts[^1]] = parts[1];
                            break;
                        case "UNSUB":
                            _ = subscriptions.Remove(parts[1]);
                            break;
                        case "PUB" or "HPUB":
                            var headed = parts[0].Equals("HPUB", StringComparison.OrdinalIgnoreCase);
                            var total = int.Parse(parts[^1], CultureInfo.InvariantCulture);
                            var headerLength = headed ? int.Parse(parts[^2], CultureInfo.InvariantCulture) : 0;
                            var reply = parts.Length == (headed ? 5 : 4) ? parts[2] : null;
                            var body = new byte[total + 2];
                            await stream.ReadExactlyAsync(body, cancellationToken);
                            var answer = Handle(parts[1], body[..headerLength], body[headerLength..total]);
                            if (answer is null)
                            {
                                _ = Interlocked.Increment(ref _violations);
                                await WriteAsync(stream, $"-ERR 'Permissions Violation for Publish to \"{parts[1]}\"'\r\n", [], cancellationToken);
                            }
                            else if (reply is not null && subscriptions.FirstOrDefault(subscription => Matches(subscription.Value, reply)).Key is { } sid)
                            {
                                var data = Encoding.UTF8.GetBytes(answer.ToJsonString());
                                await WriteAsync(stream, $"MSG {reply} {sid} {data.Length}\r\n", data, cancellationToken);
                            }

                            break;
                    }
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or EndOfStreamException)
            {
                // The client or the test went away.
            }
        }
    }

    // The answer to a publish, or null when the terrabase role may not publish there.
    private JsonObject? Handle(string subject, byte[] headers, byte[] data)
    {
        lock (_gate)
        {
            if (subject.StartsWith(Subjects, StringComparison.Ordinal))
            {
                _last[subject] = (++_sequence, headers, data);
                return new JsonObject { ["stream"] = Stream, ["seq"] = _sequence };
            }

            if (subject == "$JS.API.STREAM.INFO." + Stream)
            {
                var filter = data.Length > 0 ? (string?)JsonNode.Parse(data)?["subjects_filter"] : null;
                var subjects = new JsonObject();
                foreach (var key in _last.Keys.Where(key => filter is null || key.StartsWith(filter.TrimEnd('>'), StringComparison.Ordinal)))
                {
                    subjects[key] = 1;
                }

                return new JsonObject
                {
                    ["type"] = "io.nats.jetstream.api.v1.stream_info_response",
                    ["config"] = new JsonObject
                    {
                        ["name"] = Stream,
                        ["subjects"] = new JsonArray(Subjects + ">"),
                        ["retention"] = "limits",
                        ["max_consumers"] = -1,
                        ["max_msgs"] = -1,
                        ["max_bytes"] = -1,
                        ["max_age"] = 15_000_000_000,
                        ["max_msgs_per_subject"] = 1,
                        ["max_msg_size"] = -1,
                        ["storage"] = "memory",
                        ["discard"] = "new",
                        ["num_replicas"] = 1,
                        ["duplicate_window"] = 15_000_000_000,
                        ["deny_delete"] = true,
                        ["allow_rollup_hdrs"] = true,
                        ["allow_direct"] = false,
                    },
                    ["created"] = "2026-10-07T00:00:00Z",
                    ["state"] = new JsonObject
                    {
                        ["messages"] = _last.Count,
                        ["bytes"] = 0,
                        ["first_seq"] = 1,
                        ["last_seq"] = _sequence,
                        ["consumer_count"] = 0,
                        ["num_subjects"] = subjects.Count,
                        ["subjects"] = subjects.Count == 0 ? null : subjects,
                    },
                };
            }

            if (subject == "$JS.API.STREAM.MSG.GET." + Stream)
            {
                var wanted = (string?)JsonNode.Parse(data)?["last_by_subj"] ?? "";
                if (!_last.TryGetValue(wanted, out var message))
                {
                    return new JsonObject
                    {
                        ["type"] = "io.nats.jetstream.api.v1.stream_msg_get_response",
                        ["error"] = new JsonObject { ["code"] = 404, ["err_code"] = 10037, ["description"] = "no message found" },
                    };
                }

                var found = new JsonObject { ["subject"] = wanted, ["seq"] = message.Sequence, ["data"] = Convert.ToBase64String(message.Data), ["time"] = "2026-10-07T00:00:00Z" };
                if (message.Headers.Length > 0)
                {
                    found["hdrs"] = Convert.ToBase64String(message.Headers);
                }

                return new JsonObject { ["type"] = "io.nats.jetstream.api.v1.stream_msg_get_response", ["message"] = found };
            }

            return null;
        }
    }

    private static bool Matches(string pattern, string subject)
    {
        var tokens = subject.Split('.');
        var wanted = pattern.Split('.');
        for (var i = 0; i < wanted.Length; i++)
        {
            if (wanted[i] == ">")
            {
                return tokens.Length > i;
            }

            if (i >= tokens.Length || (wanted[i] != "*" && wanted[i] != tokens[i]))
            {
                return false;
            }
        }

        return tokens.Length == wanted.Length;
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        var next = new byte[1];
        while (await stream.ReadAsync(next, cancellationToken) == 1)
        {
            if (next[0] == '\n')
            {
                return Encoding.UTF8.GetString([.. line]).TrimEnd('\r');
            }

            line.Add(next[0]);
        }

        return null;
    }

    private static async Task WriteAsync(Stream stream, string line, byte[] payload, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken);
        if (payload.Length > 0 || line.StartsWith("MSG ", StringComparison.Ordinal))
        {
            await stream.WriteAsync(payload, cancellationToken);
            await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }
}
