using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Amazon.Util;
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
public sealed partial class FakeS3
{
    public const string Bucket = "backup-test";
    public const string CustomerBucket = "customer-backup";
    public const string CustomerEndpoint = "https://customer-storage.test";
    public const string CustomerAccessKey = "customer-access";
    public const string CustomerSecretKey = "customer-secret";
    private const string R2Storage = "https://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com";
    private const string PlatformAccessKey = "test-access-key";
    private const string PlatformSecretKey = "test-secret-key";
    private const string ShapeAccessKey = "test-shape-access-key";
    private const string ShapeSecretKey = "test-shape-secret-key";
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
    private static readonly IComparer<byte[]> S3KeyOrder = Comparer<byte[]>.Create((left, right) => left.AsSpan().SequenceCompareTo(right));

    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, KeyRequests> _keyRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _customerObjects = new(StringComparer.Ordinal);
    private readonly byte[] _masterKey = RandomNumberGenerator.GetBytes(32);
    private readonly List<(string Version, DateTimeOffset At)> _snapshots = [];
    private readonly ConcurrentDictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, int> _partRequests = new();
    // User metadata and ETag of completed multipart objects, attached to their exact content: replacing or deleting the object
    // drops them with it, and they never keep a deleted body alive.
    private readonly ConditionalWeakTable<byte[], UploadedObject> _uploadedMetadata = [];
    private int _startedUploads;
    private int _completedUploads;
    private int _abortedUploads;
    private int _badSignatures;
    private int _bucketDeletes;
    private int _inconsistentAnchorReads;
    private long _snapshotVersion = 1_791_000_000_000_000_000;
    private int _generatedKeys;
    private int _listRequests;
    private string? _lastListStartAfter;

    /// <summary>Objects the bucket's retention rule protects from deletion, answered <c>RetentionRuleViolation</c>.</summary>
    public Func<string, bool> Locked { get; set; } = _ => false;

    /// <summary>Object PUTs refused with <c>AccessDenied</c>, without storing their content.</summary>
    public Func<string, bool> FailPut { get; set; } = _ => false;

    public Func<string, HttpStatusCode?>? FailShapeDelete { get; set; }

    public bool Expire(string key) => _objects.TryRemove(key, out _);

    /// <summary>Platform anchor GETs missing Tigris's consistent-read header.</summary>
    public int InconsistentAnchorReads => Volatile.Read(ref _inconsistentAnchorReads);

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

    /// <summary>Multipart part uploads answered 500 <c>InternalError</c> without storing the part; receives the key and part number.</summary>
    public Func<string, int, bool> FailPart { get; set; } = (_, _) => false;

    /// <summary>An optional barrier before a part is checked and stored; receives the key and part number, never the content.</summary>
    public Func<string, int, CancellationToken, Task>? BeforePart { get; set; }

    /// <summary>Runs after a part is stored; receives the key and part number.</summary>
    public Action<string, int>? PartStored { get; set; }

    /// <summary>Multipart aborts answered 500 <c>InternalError</c>, leaving the upload pending; receives the key.</summary>
    public Func<string, bool> FailAbort { get; set; } = _ => false;

    /// <summary>
    /// Answers a multipart completion with 200 and the returned XML body instead of completing it, such as an
    /// <c>&lt;Error&gt;</c> body (how some S3-compatible services report a failed completion) or a success result that stored
    /// nothing; the upload stays pending. Receives the key; null completes normally.
    /// </summary>
    public Func<string, string?>? CompleteAnswer { get; set; }

    /// <summary>Replaces the <c>Content-Length</c> an object HEAD reports for an existing key; null reports its stored length.</summary>
    public Func<string, long?>? HeadLengthOverride { get; set; }

    /// <summary>Completed multipart objects keep no user metadata, like S3-compatible stores that drop it.</summary>
    public bool DropUploadMetadata { get; set; }

    /// <summary>
    /// S3-style ETags: a part's is its MD5, and a completed multipart object's is the MD5 of its parts' binary MD5s, then
    /// <c>-</c> and the part count. Otherwise every ETag is an opaque UUID, like stores whose ETags are not MD5s.
    /// </summary>
    public bool Md5ETags { get; set; }

    /// <summary>Part upload requests by part number, failed ones included.</summary>
    public IReadOnlyDictionary<int, int> PartRequests => _partRequests;

    /// <summary>Multipart uploads started (CreateMultipartUpload).</summary>
    public int StartedUploads => Volatile.Read(ref _startedUploads);

    /// <summary>Multipart uploads completed into an object.</summary>
    public int CompletedUploads => Volatile.Read(ref _completedUploads);

    /// <summary>Multipart uploads aborted.</summary>
    public int AbortedUploads => Volatile.Read(ref _abortedUploads);

    /// <summary>Multipart uploads neither completed nor aborted; their parts are not objects.</summary>
    public int PendingUploads => _uploads.Count;

    /// <summary>The keys of <see cref="PendingUploads"/>.</summary>
    public IReadOnlyList<string> PendingUploadKeys => [.. _uploads.Values.Select(upload => upload.Key)];

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

    /// <summary>Refuses snapshot expiry with 403 while true.</summary>
    public bool RejectSnapshotDeletes { get; set; }

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

    /// <summary>Seeds an object in this test's bucket, including historical keys whose timestamp is encoded in the key.</summary>
    public void SeedObject(bool customer, string key, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        (customer ? _customerObjects : _objects)[key] = [.. content];
    }

    /// <summary>Sets every backup setting to this fake.</summary>
    /// <summary>
    /// Serves the platform bucket at an R2 account endpoint, so the server links shape objects through the CDN and purges
    /// them through the Cloudflare API (<see cref="FakeCloudflare"/>).
    /// </summary>
    public bool R2 { get; init; }

    private string ObjectStorage => R2 ? R2Storage : "https://objectstorage.test";

    public void Configure(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings["backup-endpoint"] = ObjectStorage;
        settings["shapes-access-key-id"] = ShapeAccessKey;
        settings["shapes-secret-access-key"] = ShapeSecretKey;
        settings["shapes-cdn-api-token"] = FakeCloudflare.Token;

        if (R2)
        {
            settings["cloudflare-api-token"] = FakeCloudflare.Token;
        }
        settings["backup-region"] = "test-region-1";
        settings["backup-bucket"] = Bucket;
        settings["backup-access-key-id"] = PlatformAccessKey;
        settings["backup-secret-key"] = PlatformSecretKey;
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
        RouteShapes(services);
        services.Replace(ServiceDescriptor.Singleton<IMasterKey>(provider => new CountingMasterKey(new MasterKey(provider.GetRequiredService<BackupSettings>().MasterKey), this)));
    }

    public void RouteShapes(IServiceCollection services)
    {
        services.AddSingleton(new ShapeStorageSettings
        {
            Endpoint = new Uri(ObjectStorage),
            Region = "test-region-1",
            Bucket = Bucket,
            AccessKeyId = ShapeAccessKey,
            SecretKey = ShapeSecretKey,
            CloudflareApiToken = FakeCloudflare.Token,
            Cdn = R2 ? new Uri("https://" + ShapeStorageSettings.CdnHost + "/") : null,
        });
        services.AddHttpClient("shapes-s3").ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
    }

    public void RejectShapeCredentials(IServiceCollection services) => services.AddSingleton(new ShapeStorageSettings
    {
        Endpoint = new Uri(ObjectStorage),
        Region = "test-region-1",
        Bucket = Bucket,
        AccessKeyId = ShapeAccessKey,
        SecretKey = ShapeSecretKey + "-revoked",
        CloudflareApiToken = FakeCloudflare.Token,
        Cdn = R2 ? new Uri("https://" + ShapeStorageSettings.CdnHost + "/") : null,
    });

    // One exact allocation for a body of known length (ReadAsByteArrayAsync buffers it, then copies): parts are 128 MiB, and
    // the suite's parallel tests share one process.
    private static async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is not { } length)
        {
            return await content.ReadAsByteArrayAsync(cancellationToken);
        }

        var body = new byte[length];
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        await stream.ReadExactlyAsync(body, cancellationToken);
        return body;
    }

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await ReadBodyAsync(request.Content, cancellationToken);
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

        return uri.GetLeftPart(UriPartial.Authority) is var authority && (authority == ObjectStorage || authority == CustomerEndpoint)
            ? await ObjectResponseAsync(request, body, cancellationToken)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> ObjectResponseAsync(HttpRequestMessage request, byte[] body, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        var (bucket, key) = slash < 0 ? (path, "") : (path[..slash], path[(slash + 1)..]);
        var customer = uri.GetLeftPart(UriPartial.Authority) == CustomerEndpoint;
        var objects = customer ? _customerObjects : _objects;
        if (bucket != (customer ? CustomerBucket : Bucket) && !IsPurgeBucket(customer, bucket))
        {
            return S3Error(HttpStatusCode.NotFound, "NoSuchBucket");
        }

        if (!customer && key.StartsWith("v1/shapes/", StringComparison.Ordinal) && !uri.Query.Contains("X-Amz-", StringComparison.OrdinalIgnoreCase)
            && !S3SignatureVerifies(request, body, ShapeAccessKey, ShapeSecretKey))
        {
            return S3Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
        }

        if (customer && !S3SignatureVerifies(request, body))
        {
            Interlocked.Increment(ref _badSignatures);
            return S3Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
        }

        // A presigned platform URL carries its SigV4 in the query; it is verified before the object is touched.
        if (!customer && uri.Query.Contains("X-Amz-", StringComparison.OrdinalIgnoreCase) && !PresignedSignatureVerifies(request))
        {
            return S3Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
        }

        ObjectRequested?.Invoke(customer, request.Method, key);

        if (await DeletePurgeVersionAsync(request, customer, key, cancellationToken) is { } versionDeleted)
        {
            return versionDeleted;
        }

        if (key.Length == 0 && request.Method == HttpMethod.Get && uri.Query.TrimStart('?').Split('&').Any(part => part is "versions" or "versions="))
        {
            return PurgeVersions(customer, bucket, System.Web.HttpUtility.ParseQueryString(uri.Query));
        }

        if (key.Length == 0 && request.Method == HttpMethod.Get && uri.Query.TrimStart('?').Split('&').Any(part => part is "uploads" or "uploads="))
        {
            return PurgeUploads(customer, bucket, System.Web.HttpUtility.ParseQueryString(uri.Query));
        }

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

        if (System.Web.HttpUtility.ParseQueryString(uri.Query) is var multipart && (multipart["uploadId"] is not null || request.Method == HttpMethod.Post))
        {
            return await MultipartResponseAsync(request, customer, bucket, key, multipart, body, cancellationToken);
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

            // Attached before the store, so a reader that finds the object also finds its metadata.
            _uploadedMetadata.AddOrUpdate(body, new(UploadHeaders(request), ETag(body)));
            if (Header(request, "If-None-Match") == "*")
            {
                if (!objects.TryAdd(key, body))
                {
                    return S3Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
                }
            }
            else
            {
                objects[key] = body;
            }

            var put = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            put.Headers.ETag = new(ETag(body));
            return put;
        }

        if (request.Method == HttpMethod.Get)
        {
            if (!customer && key.StartsWith("catalog-anchor/", StringComparison.Ordinal) && Header(request, "X-Tigris-Consistent") != "true")
            {
                Interlocked.Increment(ref _inconsistentAnchorReads);
            }

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
            foreach (var (name, value) in _uploadedMetadata.TryGetValue(stored, out var uploaded) ? uploaded.Metadata : [])
            {
                if (name is "content-type" or "content-encoding" or "content-disposition")
                {
                    get.Content.Headers.TryAddWithoutValidation(name, value);
                }
                else
                {
                    get.Headers.Add(name, value);
                }
            }

            if (customer)
            {
                CustomerGetResponseCreated?.Invoke(key, get);
            }

            return get;
        }

        if (request.Method == HttpMethod.Head)
        {
            var head = new HttpResponseMessage(key.Length == 0 || objects.ContainsKey(key) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            if (objects.TryGetValue(key, out var headed))
            {
                // Like S3, a HEAD of an object reports its length, its ETag, and the user metadata it was uploaded with.
                head.Content = new ByteArrayContent([]);
                head.Content.Headers.ContentLength = HeadLengthOverride?.Invoke(key) ?? headed.Length;
                var uploaded = _uploadedMetadata.TryGetValue(headed, out var entry);
                head.Headers.ETag = new(uploaded ? entry!.ETag : ETag(headed));
                foreach (var (name, value) in uploaded ? entry!.Metadata : [])
                {
                    if (name is "content-type" or "content-encoding" or "content-disposition")
                    {
                        head.Content.Headers.TryAddWithoutValidation(name, value);
                    }
                    else
                    {
                        head.Headers.Add(name, value);
                    }
                }
            }

            return head;
        }

        if (customer && request.Method == HttpMethod.Delete && BeforeCustomerDelete is { } beforeDelete)
        {
            await beforeDelete(key, cancellationToken);
        }

        if (request.Method == HttpMethod.Delete && objects.ContainsKey(key) && Locked(key))
        {
            return S3Error(HttpStatusCode.Forbidden, "RetentionRuleViolation");
        }

        if (request.Method == HttpMethod.Delete && !customer && FailShapeDelete?.Invoke(key) is { } refused)
        {
            return S3Error(refused, refused == HttpStatusCode.Forbidden ? "AccessDenied" : "InternalError");
        }

        if (request.Method == HttpMethod.Delete)
        {
            // Like S3: deleting a missing key succeeds too.
            objects.TryRemove(key, out _);
            return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) };
        }

        return S3Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed");
    }

    // S3 multipart uploads. Parts may arrive in any order and be replaced; Complete must list every part from 1 with the ETag it
    // was stored under, and every part but the last must hold at least 5 MiB, as S3 requires.
    private async Task<HttpResponseMessage> MultipartResponseAsync(
        HttpRequestMessage request, bool customer, string bucket, string key, NameValueCollection query, byte[] body, CancellationToken cancellationToken)
    {
        var method = request.Method;
        if (query["uploadId"] is not { } id)
        {
            id = Guid.NewGuid().ToString("N");
            _uploads[id] = new Upload(customer, key, Time.GetUtcNow(), UploadHeaders(request));
            Interlocked.Increment(ref _startedUploads);
            return Xml(HttpStatusCode.OK, new XElement(S3 + "InitiateMultipartUploadResult",
                new XElement(S3 + "Bucket", bucket), new XElement(S3 + "Key", key), new XElement(S3 + "UploadId", id)));
        }

        if (!_uploads.TryGetValue(id, out var upload) || upload.Customer != customer || upload.Key != key)
        {
            return S3Error(HttpStatusCode.NotFound, "NoSuchUpload");
        }

        if (method == HttpMethod.Put)
        {
            var number = int.Parse(query["partNumber"]!, CultureInfo.InvariantCulture);
            _partRequests.AddOrUpdate(number, 1, (_, count) => count + 1);
            if (BeforePart is { } beforePart)
            {
                await beforePart(key, number, cancellationToken);
            }

            if (FailPart(key, number))
            {
                return S3Error(HttpStatusCode.InternalServerError, "InternalError");
            }

            upload.Parts[number] = body;
            PartStored?.Invoke(key, number);
            var stored = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            stored.Headers.ETag = new(PartETag(body));
            return stored;
        }

        if (method == HttpMethod.Delete)
        {
            if (FailAbort(key))
            {
                return S3Error(HttpStatusCode.InternalServerError, "InternalError");
            }

            if (_uploads.TryRemove(id, out _))
            {
                Interlocked.Increment(ref _abortedUploads);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) };
        }

        if (CompleteAnswer?.Invoke(key) is { } answer)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/xml") };
        }

        var listed = XElement.Parse(Encoding.UTF8.GetString(body)).Elements().Where(item => item.Name.LocalName == "Part")
            .Select(item => (Number: int.Parse(Child(item, "PartNumber"), CultureInfo.InvariantCulture), ETag: Child(item, "ETag"))).ToList();
        if (listed.Count == 0 || listed.Where((item, index) => item.Number != index + 1
            || !upload.Parts.TryGetValue(item.Number, out var part) || PartETag(part) != item.ETag).Any())
        {
            return S3Error(HttpStatusCode.BadRequest, "InvalidPart");
        }

        var parts = listed.Select(item => upload.Parts[item.Number]).ToList();
        if (parts.SkipLast(1).Any(part => part.Length < 5 << 20))
        {
            return S3Error(HttpStatusCode.BadRequest, "EntityTooSmall");
        }

        if (!_uploads.TryRemove(id, out _))
        {
            return S3Error(HttpStatusCode.NotFound, "NoSuchUpload");
        }

        var content = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(content, offset);
            offset += part.Length;
        }

        var etag = Md5ETags
            ? $"\"{Convert.ToHexStringLower(CryptoUtilFactory.CryptoInstance.ComputeMD5Hash([.. parts.SelectMany(CryptoUtilFactory.CryptoInstance.ComputeMD5Hash)]))}-{parts.Count}\""
            : ETag(content);
        _uploadedMetadata.AddOrUpdate(content, new(DropUploadMetadata ? [] : upload.Metadata, etag));
        (customer ? _customerObjects : _objects)[key] = content;
        Interlocked.Increment(ref _completedUploads);
        return Xml(HttpStatusCode.OK, new XElement(S3 + "CompleteMultipartUploadResult",
            new XElement(S3 + "Bucket", bucket), new XElement(S3 + "Key", key), new XElement(S3 + "ETag", etag)));
    }

    private string PartETag(byte[] part) => Md5ETags ? $"\"{Convert.ToHexStringLower(CryptoUtilFactory.CryptoInstance.ComputeMD5Hash(part))}\"" : ETag(part);

    // ListMultipartUploads in S3's order (key, then upload id), resuming after key-marker and upload-id-marker. Like a store
    // that pages early, one upload per page, so a caller must follow the markers.
    private HttpResponseMessage ListUploads(bool customer, string bucket, NameValueCollection query)
    {
        var prefix = query["prefix"] ?? "";
        var (keyMarker, idMarker) = (query["key-marker"] ?? "", query["upload-id-marker"] ?? "");
        var markerBytes = Encoding.UTF8.GetBytes(keyMarker);
        var listed = _uploads.Where(item => item.Value.Customer == customer && item.Value.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(item => (Id: item.Key, item.Value, Bytes: Encoding.UTF8.GetBytes(item.Value.Key)))
            .Where(item => item.Bytes.AsSpan().SequenceCompareTo(markerBytes) is var order && (order > 0 || order == 0 && string.CompareOrdinal(item.Id, idMarker) > 0))
            .OrderBy(item => item.Bytes, S3KeyOrder).ThenBy(item => item.Id, StringComparer.Ordinal).Take(2).ToList();
        var page = listed.Take(1).ToList();
        var truncated = listed.Count > 1;
        return Xml(HttpStatusCode.OK, new XElement(S3 + "ListMultipartUploadsResult",
            new XElement(S3 + "Bucket", bucket),
            new XElement(S3 + "KeyMarker", keyMarker),
            new XElement(S3 + "UploadIdMarker", idMarker),
            truncated ? new XElement(S3 + "NextKeyMarker", page[0].Value.Key) : null,
            truncated ? new XElement(S3 + "NextUploadIdMarker", page[0].Id) : null,
            new XElement(S3 + "Prefix", prefix),
            new XElement(S3 + "MaxUploads", 1),
            new XElement(S3 + "IsTruncated", truncated ? "true" : "false"),
            page.Select(item => new XElement(S3 + "Upload",
                new XElement(S3 + "Key", item.Value.Key),
                new XElement(S3 + "UploadId", item.Id),
                new XElement(S3 + "Initiated", item.Value.Initiated.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
                new XElement(S3 + "StorageClass", "STANDARD")))));
    }

    private static string Child(XElement element, string name) => element.Elements().Single(child => child.Name.LocalName == name).Value;

    // Presigned platform URL (SigV4 in the query): the signature covers every query parameter but X-Amz-Signature, the payload
    // is UNSIGNED-PAYLOAD, and the URL is valid from X-Amz-Date for 1 to 600 seconds on this fake's clock.
    private bool PresignedSignatureVerifies(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var credential = query["X-Amz-Credential"]?.Split('/');
        var shape = uri.AbsolutePath.Contains("/v1/shapes/", StringComparison.Ordinal);
        if (query["X-Amz-Algorithm"] != "AWS4-HMAC-SHA256" || credential is not [var accessKey, _, "test-region-1", "s3", "aws4_request"]
            || accessKey != (shape ? ShapeAccessKey : PlatformAccessKey)
            || query["X-Amz-SignedHeaders"] != "host" || query["X-Amz-Signature"] is not { } signature
            || query["X-Amz-Date"] is not { } date || !DateTimeOffset.TryParseExact(date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var signedAt)
            || query["X-Amz-Expires"] is not { } expires || !int.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var lifetime) || lifetime is < 1 or > 600)
        {
            return false;
        }

        if (date[..8] != credential[1] || Time.GetUtcNow() > signedAt.AddSeconds(lifetime))
        {
            return false;
        }

        return signature == SigV4Signature(request, shape ? ShapeSecretKey : PlatformSecretKey, credential, date, "host", "UNSIGNED-PAYLOAD", CanonicalQuery(uri, "X-Amz-Signature"));
    }

    // Partial SigV4: checks customer credentials, payload hash and region, but not required SignedHeaders or
    // scope-date freshness. Path/query canonicalization covers current AWS-URL-safe ULID/digit keys only;
    // extend before testing keys or query values requiring percent encoding.
    private static bool S3SignatureVerifies(HttpRequestMessage request, byte[] body, string accessKey = CustomerAccessKey, string secretKey = CustomerSecretKey)
    {
        if (Header(request, "authorization") is not { } authorization || !authorization.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal))
        {
            return false;
        }

        var fields = authorization["AWS4-HMAC-SHA256 ".Length..].Split(',')
            .Select(field => field.Trim().Split('=', 2)).ToDictionary(field => field[0], field => field[1], StringComparer.Ordinal);
        var credential = fields.GetValueOrDefault("Credential")?.Split('/');
        if (credential is not [var suppliedKey, _, "test-region-1", "s3", "aws4_request"] || suppliedKey != accessKey
            || fields.GetValueOrDefault("SignedHeaders") is not { } signedHeaders || fields.GetValueOrDefault("Signature") is not { } signature
            || Header(request, "x-amz-date") is not { } date || Header(request, "x-amz-content-sha256") is not { } payloadHash)
        {
            return false;
        }

        if (payloadHash != "UNSIGNED-PAYLOAD" && payloadHash != Convert.ToHexStringLower(SHA256.HashData(body)))
        {
            return false;
        }

        return signature == SigV4Signature(request, secretKey, credential, date, signedHeaders, payloadHash, CanonicalQuery(request.RequestUri!, null));
    }

    // The query in SigV4 canonical form: percent-encoded names and values, sorted, without the excluded parameter name.
    private static string CanonicalQuery(Uri uri, string? excluded) => string.Join('&', uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .Select(parts => (Key: Uri.EscapeDataString(System.Web.HttpUtility.UrlDecode(parts[0])),
            Value: Uri.EscapeDataString(System.Web.HttpUtility.UrlDecode(parts.Length == 2 ? parts[1] : ""))))
        .Where(part => part.Key != excluded)
        .OrderBy(part => part.Key, StringComparer.Ordinal).ThenBy(part => part.Value, StringComparer.Ordinal)
        .Select(part => part.Key + "=" + part.Value));

    // The SigV4 signature for a request, from the secret and the credential scope [access key, date, region, service, terminator].
    private static string SigV4Signature(HttpRequestMessage request, string secret, string[] credential, string date, string signedHeaders, string payloadHash, string canonicalQuery)
    {
        var uri = request.RequestUri!;
        var canonicalHeaders = signedHeaders.Split(';').Select(name =>
            $"{name}:{(name == "host" ? request.Headers.Host ?? uri.Authority : Header(request, name))?.Trim()}\n");
        var canonical = string.Join('\n', request.Method.Method,
            string.Join('/', uri.AbsolutePath.Split('/').Select(part => Uri.EscapeDataString(Uri.UnescapeDataString(part)))),
            canonicalQuery, string.Concat(canonicalHeaders), signedHeaders, payloadHash);
        var scope = string.Join('/', credential[1..]);
        var signed = $"AWS4-HMAC-SHA256\n{date}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secret), Encoding.UTF8.GetBytes(credential[1]));
        foreach (var component in credential[2..])
        {
            key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(component));
        }

        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signed)));
    }

    // The user metadata, Cache-Control and content headers an upload stores; HEAD and GET return them with the object.
    private static KeyValuePair<string, string>[] UploadHeaders(HttpRequestMessage request) => [.. request.Headers
        .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
        .Where(header => header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
        .Select(header => KeyValuePair.Create(header.Key.ToLowerInvariant(), string.Join(",", header.Value)))];

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
                if (RejectSnapshotDeletes)
                {
                    return S3Error(HttpStatusCode.Forbidden, "AccessDenied");
                }

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

    private sealed record UploadedObject(KeyValuePair<string, string>[] Metadata, string ETag);

    private sealed record Upload(bool Customer, string Key, DateTimeOffset Initiated, KeyValuePair<string, string>[] Metadata)
    {
        public ConcurrentDictionary<int, byte[]> Parts { get; } = new();
    }

    private sealed class Handler(FakeS3 s3) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            s3.RespondAsync(request, cancellationToken);
    }
}
