using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Features.Backup;

namespace TerraBase.Testing;

/// <summary>
/// The platform and customer S3-compatible buckets (path-style), in memory, for continuous backup. <see cref="Route"/>
/// serves both through message handlers that replace the server's transports (no socket), and counts the master key's
/// data key generations and unwraps.
/// </summary>
public sealed class FakeS3
{
    public const string Bucket = "backup-test";
    public const string CustomerBucket = "customer-backup";
    public const string CustomerEndpoint = "https://customer-storage.test";
    public const string CustomerAccessKey = "customer-access";
    public const string CustomerSecretKey = "customer-secret";
    private const string ObjectStorage = "https://objectstorage.test";
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
    private static readonly IComparer<byte[]> S3KeyOrder = Comparer<byte[]>.Create((left, right) => left.AsSpan().SequenceCompareTo(right));

    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, KeyRequests> _keyRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _customerObjects = new(StringComparer.Ordinal);
    private readonly byte[] _masterKey = RandomNumberGenerator.GetBytes(32);
    private readonly List<(string Version, DateTimeOffset At)> _snapshots = [];
    private int _badSignatures;
    private int _bucketDeletes;
    private long _snapshotVersion = 1_791_000_000_000_000_000;
    private int _generatedKeys;
    private int _listRequests;
    private string? _lastListStartAfter;

    /// <summary>Objects the bucket's retention rule protects from deletion, answered <c>RetentionRuleViolation</c>.</summary>
    public Func<string, bool> Locked { get; set; } = _ => false;

    /// <summary>Object PUTs refused with <c>AccessDenied</c>, without storing their content.</summary>
    public Func<string, bool> FailPut { get; set; } = _ => false;

    /// <summary>An optional asynchronous barrier before an object PUT; receives its bucket key, never its content.</summary>
    public Func<string, CancellationToken, Task>? BeforePut { get; set; }

    /// <summary>An optional asynchronous barrier before an object GET returns; receives the bucket key, never its content.</summary>
    public Func<string, CancellationToken, Task>? BeforeGet { get; set; }

    /// <summary>An optional asynchronous barrier before an object LIST returns; receives the requested prefix.</summary>
    public Func<string, CancellationToken, Task>? BeforeList { get; set; }

    /// <summary>The single prefix whose LIST count and last cursor are observed.</summary>
    public string? ObservedListPrefix { get; set; }

    /// <summary>LIST requests for <see cref="ObservedListPrefix"/>.</summary>
    public int ListRequests => Volatile.Read(ref _listRequests);

    /// <summary>The last <c>start-after</c> for the observed prefix; empty means a full listing.</summary>
    public string? LastListStartAfter => Volatile.Read(ref _lastListStartAfter);

    /// <summary>Customer PUTs refused with AccessDenied, to simulate a destination becoming unavailable.</summary>
    public Func<string, bool> RejectCustomerWrites { get; set; } = _ => false;

    /// <summary>Replaces a customer listing after normal prefix filtering, including deliberately foreign keys.</summary>
    public Func<string, IReadOnlyList<string>, IReadOnlyList<string>>? CustomerListingOverride { get; set; }

    /// <summary>Replaces the next token of a truncated customer page; receives its prefix and normal token.</summary>
    public Func<string, string, string>? CustomerContinuationTokenOverride { get; set; }

    /// <summary>Overrides a listed customer object's size, including malformed negative sizes.</summary>
    public Func<string, long?>? CustomerListingSizeOverride { get; set; }

    /// <summary>Overrides a customer GET's S3 error code with a 404, to distinguish a missing bucket from a missing key.</summary>
    public Func<string, string?>? CustomerReadError { get; set; }

    /// <summary>Customer prefixes returning empty truncated pages with alternating continuation tokens.</summary>
    public Func<string, bool>? EmptyCustomerPages { get; set; }

    /// <summary>Replaces a customer GET body; null retains the stored object's body.</summary>
    public Func<string, byte[]?>? CustomerReadOverride { get; set; }

    /// <summary>Replaces customer GET content, including a streamed body; null retains the normal body.</summary>
    public Func<string, HttpContent?>? CustomerContentOverride { get; set; }

    /// <summary>Replaces platform GET content, including a streamed body; null retains the stored body.</summary>
    public Func<string, HttpContent?>? PlatformContentOverride { get; set; }

    /// <summary>Adjusts headers on a successful existing-object customer GET, after content and ETag creation.</summary>
    public Action<string, HttpResponseMessage>? CustomerGetResponseCreated { get; set; }

    /// <summary>Runs before a customer PUT is accepted; gates must observe the request's cancellation token.</summary>
    public Func<string, CancellationToken, Task>? BeforeCustomerWrite { get; set; }

    /// <summary>Runs before customer DELETE lock checks or mutation; observes the transport cancellation token.</summary>
    public Func<string, CancellationToken, Task>? BeforeCustomerDelete { get; set; }

    /// <summary>Observes object requests without retaining an unbounded request history; true identifies the customer.</summary>
    public Action<bool, HttpMethod, string>? ObjectRequested { get; set; }

    /// <summary>Every stored object, by key.</summary>
    public IReadOnlyDictionary<string, byte[]> Objects => _objects;

    /// <summary>Customer bucket objects, separate from the platform bucket.</summary>
    public IReadOnlyDictionary<string, byte[]> CustomerObjects => _customerObjects;

    /// <summary>Customer requests whose SigV4 signature did not verify; they were refused.</summary>
    public int BadSignatures => Volatile.Read(ref _badSignatures);

    /// <summary>Data keys the master key generated.</summary>
    public int GeneratedKeys => Volatile.Read(ref _generatedKeys);

    /// <summary>The clock Tigris snapshots are dated with; tests on a fake clock set theirs.</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Whether the platform bucket has Tigris snapshots enabled, as HeadBucket reports it.</summary>
    public bool SnapshotsEnabled { get; set; } = true;

    /// <summary>Refuses snapshot takes with 403 while true.</summary>
    public bool RejectSnapshots { get; set; }

    /// <summary>The platform bucket's Tigris snapshots, oldest first.</summary>
    public IReadOnlyList<(string Version, DateTimeOffset At)> Snapshots
    {
        get
        {
            lock (_snapshots)
            {
                return [.. _snapshots.OrderBy(snapshot => snapshot.At)];
            }
        }
    }

    /// <summary>DeleteBucket requests without a snapshot version: deleting the bucket itself, which TerraBase never asks.</summary>
    public int BucketDeletes => Volatile.Read(ref _bucketDeletes);

    /// <summary>Seeds a Tigris snapshot of the platform bucket taken at <paramref name="at"/>.</summary>
    public void SeedSnapshot(DateTimeOffset at)
    {
        lock (_snapshots)
        {
            _snapshots.Add((NextSnapshotVersion(), at));
        }
    }

    private void RecordGeneratedKey() => Interlocked.Increment(ref _generatedKeys);

    /// <summary>Observes GETs and unwraps of a persisted key, with at most 16 explicitly registered keys and no request log.</summary>
    public KeyRequests ObserveKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_keyRequests)
        {
            if (_keyRequests.TryGetValue(key, out var observed))
            {
                return observed;
            }

            if (_keyRequests.Count >= 16)
            {
                throw new InvalidOperationException("too many observed backup keys");
            }

            if (!_objects.TryGetValue(key, out var stored))
            {
                throw new ArgumentException("ObserveKey requires an already persisted backup key", nameof(key));
            }

            observed = new KeyRequests(SHA256.HashData(stored));
            _keyRequests[key] = observed;
            return observed;
        }
    }

    /// <summary>Seeds an object owned by this test, such as an unrelated file under a backup prefix.</summary>
    public void StoreObject(string key, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        _objects[key] = content;
    }

    /// <summary>Removes an object from this test's bucket, as a lifecycle rule or an operator could.</summary>
    public bool RemoveObject(string key) => _objects.TryRemove(key, out _);

    /// <summary>Seeds an object in this test's bucket, including historical keys whose timestamp is encoded in the key.</summary>
    public void SeedObject(bool customer, string key, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        (customer ? _customerObjects : _objects)[key] = [.. content];
    }

    /// <summary>Sets every backup setting to this fake.</summary>
    public void Configure(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings["backup-endpoint"] = ObjectStorage;
        settings["backup-region"] = "test-region-1";
        settings["backup-bucket"] = Bucket;
        settings["backup-access-key-id"] = "test-access-key";
        settings["backup-secret-key"] = "test-secret-key";
        settings["master-key"] = Convert.ToBase64String(_masterKey);
    }

    /// <summary>Wraps <paramref name="key"/> with this fake's master key, as a persisted backup key holds it.</summary>
    public string Wrap(byte[] key) => new MasterKey(_masterKey).Wrap(key);

    /// <summary>Unwraps a persisted backup key wrapped by this fake's master key.</summary>
    public byte[] Unwrap(string wrapped) => new MasterKey(_masterKey).Unwrap(wrapped);

    /// <summary>Routes the server's object storage clients here and counts its master key's work.</summary>
    public void Route(IServiceCollection services)
    {
        RoutePlatform(services);
        services.AddHttpClient("backup-s3-customer").ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
    }

    /// <summary>Replaces only the platform transport and the master key, leaving the real customer connection handler intact.</summary>
    public void RoutePlatform(IServiceCollection services)
    {
        services.AddHttpClient("backup-s3").ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
        services.Replace(ServiceDescriptor.Singleton<IMasterKey>(provider => new CountingMasterKey(new MasterKey(provider.GetRequiredService<BackupSettings>().MasterKey), this)));
    }

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var uri = request.RequestUri!;
        if (uri.GetLeftPart(UriPartial.Authority) == CustomerEndpoint && request.Method == HttpMethod.Put
            && BeforeCustomerWrite is { } beforeWrite)
        {
            var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            await beforeWrite(path[(path.IndexOf('/', StringComparison.Ordinal) + 1)..], cancellationToken).ConfigureAwait(false);
        }

        if (uri.GetLeftPart(UriPartial.Authority) == ObjectStorage && uri.AbsolutePath.TrimStart('/').TrimEnd('/') is "" or Bucket
            && (request.Method != HttpMethod.Get || uri.AbsolutePath == "/"))
        {
            return SnapshotResponse(request);
        }

        return uri.GetLeftPart(UriPartial.Authority) switch
        {
            ObjectStorage or CustomerEndpoint => await ObjectResponseAsync(request, body, cancellationToken),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private async Task<HttpResponseMessage> ObjectResponseAsync(HttpRequestMessage request, byte[] body, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        var (bucket, key) = slash < 0 ? (path, "") : (path[..slash], path[(slash + 1)..]);
        var customer = uri.GetLeftPart(UriPartial.Authority) == CustomerEndpoint;
        var objects = customer ? _customerObjects : _objects;
        if (bucket != (customer ? CustomerBucket : Bucket))
        {
            return S3Error(HttpStatusCode.NotFound, "NoSuchBucket");
        }

        if (customer && !S3SignatureVerifies(request, body))
        {
            Interlocked.Increment(ref _badSignatures);
            return S3Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
        }

        ObjectRequested?.Invoke(customer, request.Method, key);

        if (key.Length == 0 && request.Method == HttpMethod.Get)
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var prefix = query["prefix"] ?? "";
            var startAfter = query["start-after"] ?? "";
            var after = query["continuation-token"] ?? startAfter;
            if (prefix == ObservedListPrefix)
            {
                Volatile.Write(ref _lastListStartAfter, startAfter);
                Interlocked.Increment(ref _listRequests);
            }

            if (BeforeList is { } beforeList)
            {
                await beforeList(prefix, cancellationToken);
            }

            if (customer && EmptyCustomerPages?.Invoke(prefix) == true)
            {
                return Xml(HttpStatusCode.OK, new XElement(S3 + "ListBucketResult",
                    new XElement(S3 + "Name", bucket),
                    new XElement(S3 + "Prefix", prefix),
                    new XElement(S3 + "KeyCount", 0),
                    new XElement(S3 + "MaxKeys", 1000),
                    new XElement(S3 + "IsTruncated", "true"),
                    new XElement(S3 + "NextContinuationToken", query["continuation-token"] == "A" ? "B" : "A")));
            }

            if (query["continuation-token"] is { } token)
            {
                after = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
            }

            var maxKeys = Math.Clamp(int.Parse(query["max-keys"] ?? "1000", CultureInfo.InvariantCulture), 0, 1000);
            var afterBytes = Encoding.UTF8.GetBytes(after);
            IReadOnlyList<string> keys = [.. objects.Keys.Where(item => item.StartsWith(prefix, StringComparison.Ordinal))
                .Select(item => (Key: item, Bytes: Encoding.UTF8.GetBytes(item)))
                .Where(item => item.Bytes.AsSpan().SequenceCompareTo(afterBytes) > 0)
                .OrderBy(item => item.Bytes, S3KeyOrder).Take(1001).Select(item => item.Key)];
            if (customer && CustomerListingOverride is { } listing)
            {
                keys = listing(prefix, keys);
            }

            var truncated = maxKeys > 0 && keys.Count > maxKeys;
            keys = [.. keys.Take(maxKeys)];
            var continuation = truncated ? Base64Url.EncodeToString(Encoding.UTF8.GetBytes(keys[^1])) : null;
            if (customer && continuation is not null && CustomerContinuationTokenOverride is { } nextToken)
            {
                continuation = nextToken(prefix, continuation);
            }

            var result = new XElement(S3 + "ListBucketResult",
                new XElement(S3 + "Name", bucket),
                new XElement(S3 + "Prefix", prefix),
                new XElement(S3 + "KeyCount", keys.Count),
                new XElement(S3 + "MaxKeys", maxKeys),
                new XElement(S3 + "IsTruncated", truncated ? "true" : "false"),
                continuation is not null ? new XElement(S3 + "NextContinuationToken", continuation) : null,
                keys.Select(item => new XElement(S3 + "Contents",
                    new XElement(S3 + "Key", item),
                    new XElement(S3 + "LastModified", "2026-10-07T00:00:00.000Z"),
                    new XElement(S3 + "ETag", ETag(objects.GetValueOrDefault(item, []))),
                    new XElement(S3 + "Size", (customer ? CustomerListingSizeOverride?.Invoke(item) : null)
                        ?? (objects.TryGetValue(item, out var bytes) ? bytes.Length : 0)),
                    new XElement(S3 + "StorageClass", "STANDARD"))));
            return Xml(HttpStatusCode.OK, result);
        }

        if (request.Method == HttpMethod.Put)
        {
            if (BeforePut is { } beforePut)
            {
                await beforePut(key, cancellationToken);
            }

            if (FailPut(key) || customer && RejectCustomerWrites(key))
            {
                return S3Error(HttpStatusCode.Forbidden, "AccessDenied");
            }

            objects[key] = body;
            var put = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            put.Headers.ETag = new(ETag(body));
            return put;
        }

        if (request.Method == HttpMethod.Get)
        {
            if (!customer && _keyRequests.TryGetValue(key, out var observed))
            {
                observed.RecordGet();
            }

            if (BeforeGet is { } before)
            {
                await before(key, cancellationToken);
            }

            if (customer && CustomerReadError?.Invoke(key) is { } code)
            {
                return S3Error(HttpStatusCode.NotFound, code);
            }

            if (!objects.TryGetValue(key, out var stored))
            {
                return S3Error(HttpStatusCode.NotFound, "NoSuchKey");
            }

            var get = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = (customer ? CustomerContentOverride?.Invoke(key) : PlatformContentOverride?.Invoke(key))
                    ?? new ByteArrayContent(customer ? CustomerReadOverride?.Invoke(key) ?? stored : stored),
            };
            get.Headers.ETag = new(ETag(stored));
            get.Content.Headers.LastModified = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
            if (customer)
            {
                CustomerGetResponseCreated?.Invoke(key, get);
            }

            return get;
        }

        if (request.Method == HttpMethod.Head)
        {
            return new HttpResponseMessage(key.Length == 0 || objects.ContainsKey(key) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        }

        if (customer && request.Method == HttpMethod.Delete && BeforeCustomerDelete is { } beforeDelete)
        {
            await beforeDelete(key, cancellationToken);
        }

        if (request.Method == HttpMethod.Delete && objects.ContainsKey(key) && Locked(key))
        {
            return S3Error(HttpStatusCode.Forbidden, "RetentionRuleViolation");
        }

        if (request.Method == HttpMethod.Delete)
        {
            // Like S3: deleting a missing key succeeds too.
            objects.TryRemove(key, out _);
            return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) };
        }

        return S3Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed");
    }

    // Partial SigV4: checks customer credentials, payload hash and region, but not required SignedHeaders or
    // scope-date freshness. Path/query canonicalization covers current AWS-URL-safe ULID/digit keys only;
    // extend before testing keys or query values requiring percent encoding.
    private static bool S3SignatureVerifies(HttpRequestMessage request, byte[] body)
    {
        if (Header(request, "authorization") is not { } authorization || !authorization.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal))
        {
            return false;
        }

        var fields = authorization["AWS4-HMAC-SHA256 ".Length..].Split(',')
            .Select(field => field.Trim().Split('=', 2)).ToDictionary(field => field[0], field => field[1], StringComparer.Ordinal);
        var credential = fields.GetValueOrDefault("Credential")?.Split('/');
        if (credential is not [CustomerAccessKey, var day, "test-region-1", "s3", "aws4_request"]
            || fields.GetValueOrDefault("SignedHeaders") is not { } signedHeaders || fields.GetValueOrDefault("Signature") is not { } signature
            || Header(request, "x-amz-date") is not { } date || Header(request, "x-amz-content-sha256") is not { } payloadHash)
        {
            return false;
        }

        if (payloadHash != "UNSIGNED-PAYLOAD" && payloadHash != Convert.ToHexStringLower(SHA256.HashData(body)))
        {
            return false;
        }

        var uri = request.RequestUri!;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Select(parts => (Key: Uri.EscapeDataString(System.Web.HttpUtility.UrlDecode(parts[0])),
                Value: Uri.EscapeDataString(System.Web.HttpUtility.UrlDecode(parts.Length == 2 ? parts[1] : ""))))
            .OrderBy(part => part.Key, StringComparer.Ordinal).ThenBy(part => part.Value, StringComparer.Ordinal);
        var canonicalHeaders = signedHeaders.Split(';').Select(name =>
            $"{name}:{(name == "host" ? request.Headers.Host ?? uri.Authority : Header(request, name))?.Trim()}\n");
        var canonical = string.Join('\n', request.Method.Method,
            string.Join('/', uri.AbsolutePath.Split('/').Select(part => Uri.EscapeDataString(Uri.UnescapeDataString(part)))),
            string.Join('&', query.Select(part => part.Key + "=" + part.Value)), string.Concat(canonicalHeaders), signedHeaders, payloadHash);
        var scope = string.Join('/', credential[1..]);
        var signed = $"AWS4-HMAC-SHA256\n{date}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + CustomerSecretKey), Encoding.UTF8.GetBytes(day));
        foreach (var component in credential[2..])
        {
            key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(component));
        }

        return signature == Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signed)));
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) || request.Content?.Headers.TryGetValues(name, out values) == true
            ? string.Join(", ", values!)
            : null;

    // Like some S3-compatible stores, a UUID: not an MD5, so the SDK does not check downloads against it.
    private static string ETag(byte[] content) => $"\"{new Guid(SHA256.HashData(content).AsSpan(0, 16))}\"";

    private static HttpResponseMessage S3Error(HttpStatusCode status, string code) =>
        Xml(status, new XElement("Error", new XElement("Code", code), new XElement("Message", code)));

    // Tigris versions are numbers; these only need to be unique.
    private string NextSnapshotVersion() => Interlocked.Increment(ref _snapshotVersion).ToString(CultureInfo.InvariantCulture);

    // Tigris bucket snapshots: ListBuckets, CreateBucket and DeleteBucket with the X-Tigris-Snapshot headers.
    private HttpResponseMessage SnapshotResponse(HttpRequestMessage request)
    {
        var snapshot = Header(request, "X-Tigris-Snapshot");
        var version = Header(request, "X-Tigris-Snapshot-Version");
        lock (_snapshots)
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                head.Headers.Add("X-Tigris-Enable-Snapshot", SnapshotsEnabled ? "true" : "false");
                return head;
            }

            // Like Tigris on a bucket without snapshots.
            if (!SnapshotsEnabled && snapshot is not null)
            {
                return S3Error(HttpStatusCode.BadRequest, request.Method == HttpMethod.Get ? "InvalidArgument" : "InvalidRequest");
            }

            if (request.Method == HttpMethod.Get && snapshot == Bucket)
            {
                return Xml(HttpStatusCode.OK, new XElement(S3 + "ListAllMyBucketsResult",
                    new XElement(S3 + "Buckets", _snapshots.Select(item => new XElement(S3 + "Bucket",
                        new XElement(S3 + "Name", item.Version), new XElement(S3 + "CreationDate", item.At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)))))));
            }

            if (request.Method == HttpMethod.Put && snapshot == "true")
            {
                if (RejectSnapshots)
                {
                    return S3Error(HttpStatusCode.Forbidden, "AccessDenied");
                }

                _snapshots.Add((NextSnapshotVersion(), Time.GetUtcNow()));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            }

            if (request.Method == HttpMethod.Delete && version is not null)
            {
                return _snapshots.RemoveAll(item => item.Version == version) == 1
                    ? new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) }
                    : S3Error(HttpStatusCode.NotFound, "NoSuchVersion");
            }

            if (request.Method == HttpMethod.Delete)
            {
                Interlocked.Increment(ref _bucketDeletes);
                return S3Error(HttpStatusCode.Conflict, "BucketNotEmpty");
            }
        }

        return S3Error(HttpStatusCode.BadRequest, "InvalidRequest");
    }

    private static HttpResponseMessage Xml(HttpStatusCode status, XElement body) =>
        new(status) { Content = new StringContent(new XDocument(new XDeclaration("1.0", "UTF-8", null), body).ToString(), Encoding.UTF8, "application/xml") };

    /// <summary>Thread-safe request totals for one observed persisted key; no key material is exposed.</summary>
    public sealed class KeyRequests
    {
        private int _gets;
        private int _unwraps;

        internal KeyRequests(byte[] fingerprint)
        {
            Fingerprint = fingerprint;
        }

        internal byte[] Fingerprint { get; }

        public int Gets => Volatile.Read(ref _gets);
        public int Unwraps => Volatile.Read(ref _unwraps);

        internal void RecordGet() => Interlocked.Increment(ref _gets);
        internal void RecordUnwrap() => Interlocked.Increment(ref _unwraps);
    }

    // The real master key; generations and unwraps are counted like the requests of a remote key service.
    private sealed class CountingMasterKey(MasterKey inner, FakeS3 s3) : IMasterKey
    {
        public Task<(byte[] Key, string Wrapped)> GenerateDataKeyAsync(CancellationToken cancellationToken)
        {
            s3.RecordGeneratedKey();
            return inner.GenerateDataKeyAsync(cancellationToken);
        }

        public Task<byte[]> UnwrapAsync(string wrapped, CancellationToken cancellationToken)
        {
            var fingerprint = SHA256.HashData(Encoding.ASCII.GetBytes(wrapped));
            foreach (var observed in s3._keyRequests.Values)
            {
                if (observed.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                {
                    observed.RecordUnwrap();
                }
            }

            return inner.UnwrapAsync(wrapped, cancellationToken);
        }
    }

    private sealed class Handler(FakeS3 s3) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            s3.RespondAsync(request, cancellationToken);
    }
}
