using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using TerraBase.Primitives;

namespace TerraBase.Testing;

// Add-only convergence and acknowledged-write preservation, not a linearizability checker.
internal sealed class ReplicationHistory(int seed, string databasePath)
{
    private const int MaxOperations = 256;
    private const int MaxJsonBytes = 1024 * 1024;
    private const int ValueLength = 64;
    private const int PayloadBytes = ValueLength + 12; // ASCII {"value":"…"}, reserved before generating it.
    private const int PageLimit = 7;
    private const int MaxPages = (MaxOperations + PageLimit - 1) / PageLimit + 1;
    private const int MaxPageBytes = 64 * 1024;
    private const int MaxCursorLength = 4096;

    private readonly string _collectionPath = databasePath.TrimEnd('/') + "/collections/items/documents";
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Operation> _history = [];
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    private int _jsonBytes;
    private long _sequence;
    private bool _checking;

    public int AcknowledgedCount
    {
        get
        {
            lock (_gate)
            {
                return _history.Values.Count(operation => operation.Outcome == "acknowledged");
            }
        }
    }

    // Returns the attempted ID even when the transport outcome is indeterminate; suite cancellation still throws.
    public async Task<string> PutAsync(HttpClient client, int node, string phase, int ordinal, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(phase);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(phase.Length, 64);
        ArgumentOutOfRangeException.ThrowIfNegative(node);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        var id = ReplicationDocuments.Id($"chaos-{seed}-{phase}-{node}-{ordinal}");
        Operation operation;
        lock (_gate)
        {
            if (_checking || _history.ContainsKey(id))
            {
                throw Failure("writes during a check or repeated operation IDs are prohibited");
            }

            if (_history.Count == MaxOperations || _jsonBytes + PayloadBytes > MaxJsonBytes)
            {
                throw Failure("operation count or JSON byte budget exhausted");
            }

            _jsonBytes += PayloadBytes;
            try
            {
                var operationSeed = BinaryPrimitives.ReadInt32LittleEndian(Ulid.Parse(id).ToByteArray());
                var faker = new Faker { Random = new Randomizer(operationSeed) };
                var json = "{\"value\":\"" + faker.Random.AlphaNumeric(ValueLength) + "\"}";
                operation = new Operation(++_sequence, null, phase, node, id, json, null, null, "invoked");
                _history.Add(id, operation);
            }
            catch
            {
                _jsonBytes -= PayloadBytes;
                throw;
            }
        }

        int? status = null;
        string? etag = null;
        string? failure = null;
        var outcome = "indeterminate";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"{_collectionPath}/by-key?id={Uri.EscapeDataString(id)}", UriKind.Relative));
            request.Headers.IfNoneMatch.ParseAdd("*");
            request.Content = new StringContent(operation.Json, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            status = (int)response.StatusCode;
            var tag = response.Headers.ETag;
            etag = tag is null ? null : tag.Tag.Length <= 128 ? tag.ToString() : "<oversized>";
            if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
            {
                outcome = "failed";
                failure = $"PUT {id} returned HTTP {status}";
            }
            else
            {
                outcome = "acknowledged";
                if (tag is null || tag.IsWeak || !IsVersion(tag.Tag.Trim('"')))
                {
                    failure = $"PUT {id} returned a missing or corrupt ETag";
                }
            }
        }
        catch (HttpRequestException)
        {
            // The server may have committed before the connection failed. Never retry this ID.
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // HttpClient's own timeout is indeterminate; suite cancellation propagates through finally.
        }
        catch (FormatException)
        {
            outcome = status is 200 or 201 ? "acknowledged" : "failed";
            etag = "<invalid>";
            failure = $"PUT {id} returned a corrupt ETag";
        }
        finally
        {
            lock (_gate)
            {
                _history[id] = operation with { Complete = ++_sequence, Status = status, ETag = etag, Outcome = outcome };
            }
        }

        if (failure is not null)
        {
            throw Failure(failure);
        }

        return id;
    }

    public async Task CheckAsync(IReadOnlyList<HttpClient> clients, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentOutOfRangeException.ThrowIfLessThan(clients.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(clients.Count, 16);
        Dictionary<string, Operation> history;
        lock (_gate)
        {
            if (_checking || _history.Values.Any(operation => operation.Complete is null))
            {
                throw Failure("check requires all writes to have completed and no concurrent check");
            }

            _checking = true;
            history = new Dictionary<string, Operation>(_history, StringComparer.Ordinal);
        }

        try
        {
            Dictionary<string, SnapshotDocument>? baseline = null;
            for (var node = 0; node < clients.Count; node++)
            {
                var snapshot = await SnapshotAsync(clients[node], node, token);
                foreach (var (id, document) in snapshot)
                {
                    if (!history.TryGetValue(id, out var operation))
                    {
                        throw Failure($"node {node} contains phantom ID {id}");
                    }

                    if (document.Json != operation.Json)
                    {
                        throw Failure($"node {node} contains altered body for {id}");
                    }
                }

                foreach (var operation in history.Values.Where(operation => operation.Outcome == "acknowledged"))
                {
                    if (!snapshot.TryGetValue(operation.Id, out var document))
                    {
                        throw Failure($"node {node} is missing acknowledged ID {operation.Id}");
                    }

                    if (document.Version != operation.ETag?.Trim('"'))
                    {
                        throw Failure($"node {node} has a divergent version for acknowledged ID {operation.Id}");
                    }
                }

                if (baseline is not null && (baseline.Count != snapshot.Count
                    || baseline.Any(pair => !snapshot.TryGetValue(pair.Key, out var document) || pair.Value != document)))
                {
                    throw Failure($"node {node} snapshot diverges from node 0 (IDs, canonical bodies or versions)");
                }

                baseline ??= snapshot;
            }
        }
        finally
        {
            lock (_gate)
            {
                _checking = false;
            }
        }
    }

    private async Task<Dictionary<string, SnapshotDocument>> SnapshotAsync(HttpClient client, int node, CancellationToken token)
    {
        var snapshot = new Dictionary<string, SnapshotDocument>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var bytes = 0;
        for (var page = 0; page < MaxPages; page++)
        {
            var path = $"{_collectionPath}?limit={PageLimit}";
            if (cursor is not null)
            {
                path += "&cursor=" + Uri.EscapeDataString(cursor);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw Failure($"node {node} page {page} returned HTTP {(int)response.StatusCode}");
            }

            // Allocate a fixed reservation before reading, including one byte to detect an oversized page.
            var capacity = Math.Min(MaxPageBytes, MaxJsonBytes - bytes);
            var buffer = new byte[capacity + 1];
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var length = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(length), token)) > 0)
            {
                length += read;
                if (length > capacity)
                {
                    throw Failure($"node {node} snapshot exceeds page or total byte budget");
                }
            }

            bytes += length;
            try
            {
                using var body = JsonDocument.Parse(buffer.AsMemory(0, length),
                    new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 8 });
                var root = body.RootElement;
                var items = root.GetProperty("items");
                if (items.GetArrayLength() > PageLimit || snapshot.Count + items.GetArrayLength() > MaxOperations)
                {
                    throw Failure($"node {node} snapshot exceeds page or total item budget");
                }

                foreach (var item in items.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString();
                    var version = item.GetProperty("version").GetString();
                    if (!EntityIds.IsValid(id) || version is null || !IsVersion(version))
                    {
                        throw Failure($"node {node} page {page} contains a corrupt ID or version");
                    }

                    var document = item.GetProperty("document");
                    // All submitted objects have exactly one string member; normalize its JSON escaping.
                    if (item.GetProperty("state").GetString() != "value" || document.ValueKind != JsonValueKind.Object
                        || document.EnumerateObject().Count() != 1 || document.GetProperty("value").ValueKind != JsonValueKind.String)
                    {
                        throw Failure($"node {node} contains a corrupt JSON body for {id}");
                    }

                    var value = document.GetProperty("value").GetString()!;
                    if (value.Length != ValueLength)
                    {
                        throw Failure($"node {node} contains an altered body for {id}");
                    }

                    var json = "{\"value\":\"" + JsonEncodedText.Encode(value).ToString() + "\"}";
                    if (!snapshot.TryAdd(id, new SnapshotDocument(json, version)))
                    {
                        throw Failure($"node {node} paginated snapshot repeats ID {id}");
                    }
                }

                var next = root.GetProperty("next_cursor");
                if (next.ValueKind == JsonValueKind.Null)
                {
                    return snapshot;
                }

                cursor = next.GetString();
                if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxCursorLength || !cursors.Add(cursor)
                    || items.GetArrayLength() == 0)
                {
                    throw Failure($"node {node} page {page} contains an invalid or repeated cursor");
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException
                || error is InvalidOperationException && !error.Message.StartsWith("Replication history seed=", StringComparison.Ordinal))
            {
                throw Failure($"node {node} page {page} contains corrupt JSON, document metadata or version");
            }
        }

        throw Failure($"node {node} snapshot exceeds page budget");
    }

    private static bool IsVersion(string value) => value.Length == 37
        && value.StartsWith("tbv1-", StringComparison.Ordinal)
        && !value.AsSpan(5).ContainsAnyExcept(LowerHex);

    private InvalidOperationException Failure(string reason)
    {
        lock (_gate)
        {
            var diagnostic = new StringBuilder($"Replication history seed={seed}: {reason}\n");
            foreach (var operation in _history.Values.OrderBy(operation => operation.Invoke))
            {
                diagnostic.AppendLine($"invoke={operation.Invoke} complete={operation.Complete} phase={JsonEncodedText.Encode(operation.Phase)} "
                    + $"node={operation.Node} id={operation.Id} json={operation.Json} status={operation.Status} "
                    + $"etag={operation.ETag} outcome={operation.Outcome}");
            }

            return new InvalidOperationException(diagnostic.ToString());
        }
    }

    private sealed record Operation(long Invoke, long? Complete, string Phase, int Node, string Id, string Json,
        int? Status, string? ETag, string Outcome);

    private sealed record SnapshotDocument(string Json, string Version);
}
