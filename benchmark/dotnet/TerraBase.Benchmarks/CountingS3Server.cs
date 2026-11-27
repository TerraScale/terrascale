using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TerraBase.Benchmarks;

public readonly record struct S3Counters(
    long Gets,
    long Puts,
    long Deletes,
    long DeleteBatches,
    long Lists,
    long NotModified,
    long Conflicts,
    long UploadedBytes,
    long DownloadedBytes,
    long ManifestBytesUploaded,
    long ObjectsDeleted)
{
    public S3Counters Since(S3Counters baseline) => new(
        Gets - baseline.Gets,
        Puts - baseline.Puts,
        Deletes - baseline.Deletes,
        DeleteBatches - baseline.DeleteBatches,
        Lists - baseline.Lists,
        NotModified - baseline.NotModified,
        Conflicts - baseline.Conflicts,
        UploadedBytes - baseline.UploadedBytes,
        DownloadedBytes - baseline.DownloadedBytes,
        ManifestBytesUploaded - baseline.ManifestBytesUploaded,
        ObjectsDeleted - baseline.ObjectsDeleted);
}

internal sealed class CountingS3Server : IAsyncDisposable
{
    private const int WorkerCount = 32;
    private const int MaxKeysPerPage = 1000;
    private const int MaxKeysPerBatchDelete = 1000;
    private const string ManifestSuffix = "/manifest.json";
    private const string XmlNamespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    private readonly HttpListener _listener;
    private readonly string _bucket;
    private readonly ConcurrentDictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);
    private readonly Lock _writeGate = new();
    private readonly TaskCompletionSource _stopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task[] _workers;
    private long _etagVersion;
    private long _gets;
    private long _puts;
    private long _deletes;
    private long _deleteBatches;
    private long _lists;
    private long _notModified;
    private long _conflicts;
    private long _uploadedBytes;
    private long _downloadedBytes;
    private long _manifestBytesUploaded;
    private long _objectsDeleted;
    private long _getLatencyTicks;

    private CountingS3Server(HttpListener listener, Uri baseUri, string bucket)
    {
        _listener = listener;
        BaseUri = baseUri;
        _bucket = bucket;
        _workers = new Task[WorkerCount];
        for (var index = 0; index < WorkerCount; index++)
        {
            _workers[index] = WorkerAsync();
        }
    }

    public Uri BaseUri { get; }

    public TimeSpan GetLatency
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _getLatencyTicks));
        set => Interlocked.Exchange(ref _getLatencyTicks, value.Ticks);
    }

    public static CountingS3Server Start(string bucket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        var port = ReserveLoopbackPort();
        var prefix = string.Concat("http://127.0.0.1:", port.ToString(CultureInfo.InvariantCulture), "/");
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        return new CountingS3Server(listener, new Uri(prefix), bucket);
    }

    public S3Counters Snapshot() => new(
        Interlocked.Read(ref _gets),
        Interlocked.Read(ref _puts),
        Interlocked.Read(ref _deletes),
        Interlocked.Read(ref _deleteBatches),
        Interlocked.Read(ref _lists),
        Interlocked.Read(ref _notModified),
        Interlocked.Read(ref _conflicts),
        Interlocked.Read(ref _uploadedBytes),
        Interlocked.Read(ref _downloadedBytes),
        Interlocked.Read(ref _manifestBytesUploaded),
        Interlocked.Read(ref _objectsDeleted));

    public void Seed(string key, byte[] content, string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        lock (_writeGate)
        {
            _objects[key] = new StoredObject(content, NextETag(), contentType);
        }
    }

    public long ObjectLength(string key) => _objects.TryGetValue(key, out var stored) ? stored.Content.Length : -1;

    public int CountKeys(string prefix)
    {
        var count = 0;
        foreach (var key in _objects.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    public async ValueTask DisposeAsync()
    {
        _ = _stopSignal.TrySetResult();
        _listener.Stop();
        _listener.Close();
        await Task.WhenAll(_workers);
    }

    private static int ReserveLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private string NextETag() =>
        string.Concat("\"", Interlocked.Increment(ref _etagVersion).ToString("x32", CultureInfo.InvariantCulture), "-1\"");

    private async Task WorkerAsync()
    {
        while (!_stopSignal.Task.IsCompleted)
        {
            var next = _listener.GetContextAsync();
            var winner = await Task.WhenAny(next, _stopSignal.Task);
            if (winner != next)
            {
                _ = next.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return;
            }

            if (next.IsFaulted || next.IsCanceled)
            {
                _ = next.Exception;
                return;
            }

            await HandleAsync(await next);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        if (request.Url is not { AbsolutePath: var rawPath, Query: var query })
        {
            await WriteErrorAsync(response, 400, "InvalidRequest", "request URL is missing");
            return;
        }

        var bucketPrefix = string.Concat("/", _bucket);
        if (!rawPath.StartsWith(bucketPrefix, StringComparison.Ordinal))
        {
            await WriteErrorAsync(response, 404, "NoSuchBucket", "bucket not found");
            return;
        }

        var remainder = rawPath[bucketPrefix.Length..].TrimStart('/');
        var method = request.HttpMethod;
        if (remainder.Length == 0)
        {
            if (string.Equals(method, "GET", StringComparison.Ordinal)
                && string.Equals(request.QueryString["list-type"], "2", StringComparison.Ordinal))
            {
                await HandleListAsync(request, response);
                return;
            }

            if (string.Equals(method, "POST", StringComparison.Ordinal) && HasQueryFlag(query, "delete"))
            {
                await HandleDeleteBatchAsync(request, response);
                return;
            }

            await WriteErrorAsync(response, 405, "MethodNotAllowed", "bucket operation is not supported");
            return;
        }

        var key = Uri.UnescapeDataString(remainder);
        if (string.Equals(method, "PUT", StringComparison.Ordinal))
        {
            await HandlePutAsync(request, response, key);
        }
        else if (string.Equals(method, "GET", StringComparison.Ordinal))
        {
            await HandleGetAsync(request, response, key, headOnly: false);
        }
        else if (string.Equals(method, "HEAD", StringComparison.Ordinal))
        {
            await HandleGetAsync(request, response, key, headOnly: true);
        }
        else if (string.Equals(method, "DELETE", StringComparison.Ordinal))
        {
            HandleDelete(response, key);
        }
        else
        {
            await WriteErrorAsync(response, 405, "MethodNotAllowed", "object operation is not supported");
        }
    }

    private async Task HandleListAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        _ = Interlocked.Increment(ref _lists);
        var prefix = request.QueryString["prefix"] is { } requestedPrefix ? requestedPrefix : string.Empty;
        var continuation = request.QueryString["continuation-token"];
        var startAfter = continuation is { Length: > 0 } token ? token : request.QueryString["start-after"];
        var maxKeys = int.TryParse(request.QueryString["max-keys"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= MaxKeysPerPage
            ? parsed
            : MaxKeysPerPage;

        var matching = _objects
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)
                && (startAfter is not { Length: > 0 } after || string.CompareOrdinal(pair.Key, after) > 0))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(maxKeys + 1)
            .ToArray();
        var truncated = matching.Length > maxKeys;
        var page = truncated ? matching[..maxKeys] : matching;

        var builder = new StringBuilder(256 + (page.Length * 160));
        _ = builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>")
            .Append("<ListBucketResult xmlns=\"").Append(XmlNamespace).Append("\">")
            .Append("<Name>").Append(SecurityElement.Escape(_bucket)).Append("</Name>")
            .Append("<Prefix>").Append(SecurityElement.Escape(prefix)).Append("</Prefix>")
            .Append("<KeyCount>").Append(page.Length.ToString(CultureInfo.InvariantCulture)).Append("</KeyCount>")
            .Append("<MaxKeys>").Append(maxKeys.ToString(CultureInfo.InvariantCulture)).Append("</MaxKeys>")
            .Append("<IsTruncated>").Append(truncated ? "true" : "false").Append("</IsTruncated>");
        if (continuation is { Length: > 0 } echoed)
        {
            _ = builder.Append("<ContinuationToken>").Append(SecurityElement.Escape(echoed)).Append("</ContinuationToken>");
        }

        if (truncated)
        {
            _ = builder.Append("<NextContinuationToken>").Append(SecurityElement.Escape(page[^1].Key)).Append("</NextContinuationToken>");
        }

        foreach (var (key, item) in page)
        {
            _ = builder.Append("<Contents><Key>")
                .Append(SecurityElement.Escape(key))
                .Append("</Key><Size>")
                .Append(item.Content.Length.ToString(CultureInfo.InvariantCulture))
                .Append("</Size><ETag>")
                .Append(SecurityElement.Escape(item.ETag))
                .Append("</ETag><StorageClass>STANDARD</StorageClass></Contents>");
        }

        _ = builder.Append("</ListBucketResult>");
        var body = Encoding.UTF8.GetBytes(builder.ToString());
        _ = Interlocked.Add(ref _downloadedBytes, body.Length);
        await WriteBodyAsync(response, 200, "application/xml", body);
    }

    private async Task HandleDeleteBatchAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        _ = Interlocked.Increment(ref _deleteBatches);
        var body = await ReadBodyAsync(request);
        if (body is null)
        {
            await WriteErrorAsync(response, 400, "IncompleteBody", "aws-chunked body is malformed");
            return;
        }

        _ = Interlocked.Add(ref _uploadedBytes, body.Length);
        if (ParseXml(body) is not { Root: { } root } || !string.Equals(root.Name.LocalName, "Delete", StringComparison.Ordinal))
        {
            await WriteErrorAsync(response, 400, "MalformedXML", "delete request body is not a Delete payload");
            return;
        }

        var quiet = root.Elements().Any(static element => string.Equals(element.Name.LocalName, "Quiet", StringComparison.Ordinal)
            && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        var keys = root.Elements()
            .Where(static element => string.Equals(element.Name.LocalName, "Object", StringComparison.Ordinal))
            .Select(static element => element.Elements().FirstOrDefault(static child => string.Equals(child.Name.LocalName, "Key", StringComparison.Ordinal))?.Value)
            .ToArray();
        if (keys.Length is 0 or > MaxKeysPerBatchDelete || keys.Any(static key => string.IsNullOrEmpty(key)))
        {
            await WriteErrorAsync(response, 400, "MalformedXML", "delete request must name between 1 and 1000 keys");
            return;
        }

        var builder = new StringBuilder(128 + (keys.Length * 96));
        _ = builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>")
            .Append("<DeleteResult xmlns=\"").Append(XmlNamespace).Append("\">");
        foreach (var key in keys)
        {
            if (key is not { } name)
            {
                continue;
            }

            if (RemoveObject(name))
            {
                _ = Interlocked.Increment(ref _objectsDeleted);
            }

            if (!quiet)
            {
                _ = builder.Append("<Deleted><Key>").Append(SecurityElement.Escape(name)).Append("</Key></Deleted>");
            }
        }

        _ = builder.Append("</DeleteResult>");
        await WriteBodyAsync(response, 200, "application/xml", Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private async Task HandlePutAsync(HttpListenerRequest request, HttpListenerResponse response, string key)
    {
        _ = Interlocked.Increment(ref _puts);
        var payload = await ReadBodyAsync(request);
        if (payload is null)
        {
            await WriteErrorAsync(response, 400, "IncompleteBody", "aws-chunked body is malformed");
            return;
        }

        _ = Interlocked.Add(ref _uploadedBytes, payload.Length);
        if (key.EndsWith(ManifestSuffix, StringComparison.Ordinal))
        {
            _ = Interlocked.Add(ref _manifestBytesUploaded, payload.Length);
        }

        var ifNoneMatch = request.Headers["If-None-Match"];
        var ifMatch = request.Headers["If-Match"];
        var contentType = request.ContentType is { Length: > 0 } requestedType ? requestedType : "application/octet-stream";
        string? etag = null;
        lock (_writeGate)
        {
            var existingFound = _objects.TryGetValue(key, out var existing);
            var rejected = (string.Equals(ifNoneMatch, "*", StringComparison.Ordinal) && existingFound)
                || (ifMatch is not null && (existing is null || !ETagMatches(ifMatch, existing.ETag)));
            if (!rejected)
            {
                etag = NextETag();
                _objects[key] = new StoredObject(payload, etag, contentType);
            }
        }

        if (etag is null)
        {
            _ = Interlocked.Increment(ref _conflicts);
            await WriteErrorAsync(response, 412, "PreconditionFailed", "at least one of the pre-conditions you specified did not hold");
            return;
        }

        response.StatusCode = 200;
        response.Headers["ETag"] = etag;
        if (request.Headers["x-amz-checksum-crc32"] is { Length: > 0 } crc32)
        {
            response.Headers["x-amz-checksum-crc32"] = crc32;
        }

        if (request.Headers["x-amz-checksum-crc32c"] is { Length: > 0 } crc32c)
        {
            response.Headers["x-amz-checksum-crc32c"] = crc32c;
        }

        if (request.Headers["x-amz-checksum-sha256"] is { Length: > 0 } sha256)
        {
            response.Headers["x-amz-checksum-sha256"] = sha256;
        }

        response.ContentLength64 = 0;
        response.Close();
    }

    private async Task HandleGetAsync(HttpListenerRequest request, HttpListenerResponse response, string key, bool headOnly)
    {
        _ = Interlocked.Increment(ref _gets);
        var latency = GetLatency;
        if (latency > TimeSpan.Zero)
        {
            await Task.Delay(latency, TimeProvider.System, CancellationToken.None);
        }

        if (!_objects.TryGetValue(key, out var stored))
        {
            await WriteErrorAsync(response, 404, "NoSuchKey", "the specified key does not exist");
            return;
        }

        if (request.Headers["If-Match"] is { } ifMatch && !ETagMatches(ifMatch, stored.ETag))
        {
            _ = Interlocked.Increment(ref _conflicts);
            await WriteErrorAsync(response, 412, "PreconditionFailed", "at least one of the pre-conditions you specified did not hold");
            return;
        }

        if (request.Headers["If-None-Match"] is { } ifNoneMatch && ETagMatches(ifNoneMatch, stored.ETag))
        {
            _ = Interlocked.Increment(ref _notModified);
            response.StatusCode = 304;
            response.Headers["ETag"] = stored.ETag;
            response.Close();
            return;
        }

        var content = stored.Content;
        var start = 0;
        var length = content.Length;
        var partial = request.Headers["Range"] is { } range && TryParseRange(range, content.Length, out start, out length);

        response.StatusCode = partial ? 206 : 200;
        response.ContentType = stored.ContentType;
        response.Headers["ETag"] = stored.ETag;
        response.Headers["Accept-Ranges"] = "bytes";
        if (partial)
        {
            response.Headers["Content-Range"] = string.Create(
                CultureInfo.InvariantCulture,
                $"bytes {start}-{start + length - 1}/{content.Length}");
        }

        response.ContentLength64 = length;
        if (!headOnly)
        {
            _ = Interlocked.Add(ref _downloadedBytes, length);
            await response.OutputStream.WriteAsync(content.AsMemory(start, length));
        }

        response.Close();
    }

    private void HandleDelete(HttpListenerResponse response, string key)
    {
        _ = Interlocked.Increment(ref _deletes);
        if (RemoveObject(key))
        {
            _ = Interlocked.Increment(ref _objectsDeleted);
        }

        response.StatusCode = 204;
        response.ContentLength64 = 0;
        response.Close();
    }

    private bool RemoveObject(string key)
    {
        lock (_writeGate)
        {
            return _objects.TryRemove(key, out _);
        }
    }

    private static bool ETagMatches(string condition, string etag)
    {
        foreach (var candidate in condition.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(candidate, "*", StringComparison.Ordinal)
                || string.Equals(candidate, etag, StringComparison.Ordinal)
                || string.Equals(candidate.Trim('"'), etag.Trim('"'), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasQueryFlag(string query, string flag)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part, flag, StringComparison.Ordinal)
                || part.StartsWith(string.Concat(flag, "="), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseRange(string header, int contentLength, out int start, out int length)
    {
        start = 0;
        length = contentLength;
        const string unit = "bytes=";
        if (!header.StartsWith(unit, StringComparison.OrdinalIgnoreCase) || header.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var spec = header.AsSpan(unit.Length);
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var first = spec[..dash];
        var last = spec[(dash + 1)..];
        if (first.IsEmpty)
        {
            if (!int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return false;
            }

            var tail = Math.Min(suffix, contentLength);
            start = contentLength - tail;
            length = tail;
            return true;
        }

        if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var from) || from >= contentLength)
        {
            return false;
        }

        var to = contentLength - 1;
        if (!last.IsEmpty)
        {
            if (!int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var requestedTo) || requestedTo < from)
            {
                return false;
            }

            to = Math.Min(requestedTo, contentLength - 1);
        }

        start = from;
        length = to - from + 1;
        return true;
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpListenerRequest request)
    {
        using var buffer = request.ContentLength64 is > 0 and <= int.MaxValue
            ? new MemoryStream((int)request.ContentLength64)
            : new MemoryStream();
        await request.InputStream.CopyToAsync(buffer);
        var raw = buffer.ToArray();
        var chunked = (request.Headers["Content-Encoding"] is { } encoding && encoding.Contains("aws-chunked", StringComparison.OrdinalIgnoreCase))
            || (request.Headers["x-amz-content-sha256"] is { } sha && sha.StartsWith("STREAMING-", StringComparison.Ordinal));
        return chunked ? DecodeAwsChunked(raw) : raw;
    }

    private static byte[]? DecodeAwsChunked(byte[] raw)
    {
        using var decoded = new MemoryStream(raw.Length);
        var position = 0;
        while (position < raw.Length)
        {
            var lineEnd = raw.AsSpan(position).IndexOf("\r\n"u8);
            if (lineEnd < 0)
            {
                return null;
            }

            var header = Encoding.ASCII.GetString(raw, position, lineEnd);
            var semicolon = header.IndexOf(';', StringComparison.Ordinal);
            var sizeText = semicolon >= 0 ? header[..semicolon] : header;
            if (!int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                return null;
            }

            position += lineEnd + 2;
            if (size == 0)
            {
                return decoded.ToArray();
            }

            if (position + size > raw.Length)
            {
                return null;
            }

            decoded.Write(raw, position, size);
            position += size + 2;
        }

        return null;
    }

    private static XDocument? ParseXml(byte[] body)
    {
        try
        {
            return XDocument.Parse(Encoding.UTF8.GetString(body));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static Task WriteErrorAsync(HttpListenerResponse response, int statusCode, string code, string message) =>
        WriteBodyAsync(
            response,
            statusCode,
            "application/xml",
            Encoding.UTF8.GetBytes(string.Concat(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>",
                code,
                "</Code><Message>",
                SecurityElement.Escape(message),
                "</Message></Error>")));

    private static async Task WriteBodyAsync(HttpListenerResponse response, int statusCode, string contentType, byte[] body)
    {
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    private sealed record StoredObject(byte[] Content, string ETag, string ContentType);
}
