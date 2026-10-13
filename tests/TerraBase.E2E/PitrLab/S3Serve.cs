using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TerraBase.E2E.PitrLab;

/// <summary>
/// A disk-backed S3-compatible bucket on 127.0.0.1 over HTTPS for a separate terrabase process: exactly the subset the
/// node uses (FakeS3 is the reference): path-style PUT, GET, HEAD and DELETE of objects, ListObjectsV2 (prefix,
/// start-after, continuation-token, max-keys, in UTF-8 byte order), HeadBucket answering without Tigris snapshots, so
/// the node's daily bucket snapshot job skips, and multipart uploads (create, upload part, complete, abort; parts on disk
/// under <c>uploads/</c>, so an upload survives a restart; every part but the last at least 5 MiB, as in S3). As in S3,
/// user metadata (<c>x-amz-meta-*</c>) from PutObject and CreateMultipartUpload is kept with the object (the upload's on
/// disk until it completes) and returned by HEAD and GET; ETags are S3's: a single PUT's is its MD5, a part's its MD5,
/// and a multipart object's the MD5 of its parts' MD5s with <c>-N</c> (N parts). Payloads signed with
/// <c>x-amz-content-sha256</c> are checked before they are stored. Any credentials are accepted (a local emulator) and
/// none is logged. Everything else (ListParts, UploadPartCopy, aws-chunked bodies...) is refused loudly.
/// </summary>
internal sealed class S3Serve : IAsyncDisposable
{
    public const string Region = "local-1";
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";

    // Object sub-resources TerraBase never uses; a request naming one is refused rather than treated as a plain PUT or GET.
    // partNumber is allowed only with uploadId (UploadPart).
    private static readonly string[] SubResources = ["partNumber", "tagging", "acl", "retention", "legal-hold", "versionId", "attributes"];

    // Catalog anchors (R10a) live outside v1/ and are never deleted, expired or faulted: stored and served like any key.
    private const string CatalogAnchors = "catalog-anchor/";

    // S3's minimum size of every part but the last (EntityTooSmall).
    private const long MinPartBytes = 5L << 20;

    private readonly WebApplication _app;
    private readonly DiskBucket _bucket;
    private readonly string _uploads;
    private readonly string _name;
    private readonly PitrFaults? _faults;
    private readonly Regex? _faultKeys;
    private readonly StreamWriter _log;
    private readonly Lock _logGate = new();
    private long _faultPuts;
    private long _faultParts;

    private S3Serve(WebApplication app, DiskBucket bucket, string name, PitrFaults? faults, StreamWriter log)
    {
        _app = app;
        _bucket = bucket;
        _uploads = Directory.CreateDirectory(Path.Combine(bucket.Root, "uploads")).FullName;
        _name = name;
        _faults = faults;
        _faultKeys = faults?.KeyRegex is { } pattern ? new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) : null;
        _log = log;
    }

    public Uri Endpoint { get; private set; } = null!;

    /// <summary>The CA that signed the server certificate; trust it (SSL_CERT_FILE) to reach <see cref="Endpoint"/>.</summary>
    public X509Certificate2 Authority { get; private set; } = null!;

    public string LogPath { get; private set; } = "";

    /// <summary>Serves <paramref name="bucket"/> from <paramref name="root"/>; fault lines go to <paramref name="logPath"/>.</summary>
    public static async Task<S3Serve> StartAsync(string root, string bucket, PitrFaults? faults, string logPath, CancellationToken cancellationToken)
    {
        // The node's own rule for backup-bucket (BackupSettings.IsBucket).
        if (bucket.Length is < 3 or > 63 || !bucket.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
        {
            throw new InvalidDataException("bucket must be 3 to 63 lowercase letters, digits, dots or hyphens");
        }

        var disk = DiskBucket.Open(root, create: true);
        var (authority, certificate) = Certificates();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Snapshots are single PUTs of the whole database file.
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificate);
            });
        });
        var app = builder.Build();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        var server = new S3Serve(app, disk, bucket, faults, log) { Authority = authority, LogPath = Path.GetFullPath(logPath) };
        app.Run(server.RespondAsync);
        try
        {
            await app.StartAsync(cancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            server.Endpoint = new UriBuilder(address) { Host = "127.0.0.1" }.Uri;
            server.Log($"listening {server.Endpoint} bucket {bucket} objects {disk.Count}");
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _log.DisposeAsync();
    }

    // A CA and a server certificate it signs (SAN 127.0.0.1 and localhost), in memory; only the CA leaves, as PEM.
    private static (X509Certificate2 Authority, X509Certificate2 Server) Certificates()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(30);
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=TerraBase PitrLab local CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        var authority = caRequest.CreateSelfSigned(notBefore, notAfter);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
        using var issued = request.Create(authority, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        return (authority, issued.CopyWithPrivateKey(key));
    }

    private void Log(string line)
    {
        lock (_logGate)
        {
            _log.WriteLine($"{DateTimeOffset.UtcNow:O} {line}");
        }
    }

    private async Task RespondAsync(HttpContext context)
    {
        var request = context.Request;
        // The raw target, decoded once: Kestrel's Path keeps %2F encoded, the SDK sends keys percent-encoded.
        var target = context.Features.Get<IHttpRequestFeature>()!.RawTarget;
        var mark = target.IndexOf('?', StringComparison.Ordinal);
        var query = mark >= 0 ? target[(mark + 1)..] : "";
        var path = Uri.UnescapeDataString(mark >= 0 ? target[..mark] : target).TrimStart('/');
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        var (bucket, key) = slash < 0 ? (path, "") : (path[..slash], path[(slash + 1)..]);
        var parameters = System.Web.HttpUtility.ParseQueryString(query);
        var method = request.Method;

        if (request.Headers.ContainsKey("X-Tigris-Snapshot"))
        {
            // Like Tigris on a bucket without snapshots.
            await ErrorAsync(context, HttpStatusCode.BadRequest, HttpMethods.IsGet(method) ? "InvalidArgument" : "InvalidRequest");
            return;
        }

        if (bucket.Length == 0)
        {
            if (HttpMethods.IsGet(method))
            {
                await XmlAsync(context, HttpStatusCode.OK, new XElement(S3 + "ListAllMyBucketsResult",
                    new XElement(S3 + "Buckets", new XElement(S3 + "Bucket", new XElement(S3 + "Name", _name), new XElement(S3 + "CreationDate", "2026-10-07T00:00:00.000Z")))));
                return;
            }

            await ErrorAsync(context, HttpStatusCode.MethodNotAllowed, "MethodNotAllowed");
            return;
        }

        if (bucket != _name)
        {
            await ErrorAsync(context, HttpStatusCode.NotFound, "NoSuchBucket");
            return;
        }

        if (key.Length == 0)
        {
            await BucketAsync(context, method, parameters);
            return;
        }

        var uploadId = parameters["uploadId"];
        if (SubResources.Any(name => (uploadId is null || name != "partNumber") && Has(parameters, name)))
        {
            // Tagging, ACLs and the rest: TerraBase never sends them.
            await ErrorAsync(context, HttpStatusCode.NotImplemented, "NotImplemented");
            return;
        }

        try
        {
            _ = _bucket.PathOf(key);
        }
        catch (ArgumentException)
        {
            await ErrorAsync(context, HttpStatusCode.BadRequest, "KeyTooLongError");
            return;
        }

        if (uploadId is not null)
        {
            await MultipartAsync(context, method, key, uploadId, parameters["partNumber"]);
        }
        else if (Has(parameters, "uploads"))
        {
            await (HttpMethods.IsPost(method) ? CreateUploadAsync(context, key) : ErrorAsync(context, HttpStatusCode.NotImplemented, "NotImplemented"));
        }
        else if (HttpMethods.IsPut(method))
        {
            await PutAsync(context, key);
        }
        else if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
        {
            await GetAsync(context, key, HttpMethods.IsHead(method));
        }
        else if (HttpMethods.IsDelete(method))
        {
            _bucket.Delete(key);
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        }
        else
        {
            await ErrorAsync(context, HttpStatusCode.MethodNotAllowed, "MethodNotAllowed");
        }
    }

    private async Task BucketAsync(HttpContext context, string method, System.Collections.Specialized.NameValueCollection parameters)
    {
        if (HttpMethods.IsHead(method))
        {
            context.Response.Headers["X-Tigris-Enable-Snapshot"] = "false";
            return;
        }

        if (HttpMethods.IsDelete(method))
        {
            // Never deletes the bucket; a snapshot version names nothing here.
            await ErrorAsync(context, context.Request.Headers.ContainsKey("X-Tigris-Snapshot-Version") ? HttpStatusCode.NotFound : HttpStatusCode.Conflict,
                context.Request.Headers.ContainsKey("X-Tigris-Snapshot-Version") ? "NoSuchVersion" : "BucketNotEmpty");
            return;
        }

        if (HttpMethods.IsPut(method))
        {
            await ErrorAsync(context, HttpStatusCode.Conflict, "BucketAlreadyOwnedByYou");
            return;
        }

        if (!HttpMethods.IsGet(method) || parameters["delimiter"] is not null || (parameters["list-type"] is { } type && type != "2"))
        {
            await ErrorAsync(context, HttpStatusCode.NotImplemented, "NotImplemented");
            return;
        }

        var prefix = parameters["prefix"] ?? "";
        var startAfter = parameters["start-after"];
        var after = startAfter;
        if (parameters["continuation-token"] is { } token)
        {
            try
            {
                after = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
            }
            catch (FormatException)
            {
                await ErrorAsync(context, HttpStatusCode.BadRequest, "InvalidArgument");
                return;
            }
        }

        if (!int.TryParse(parameters["max-keys"] ?? "1000", NumberStyles.None, CultureInfo.InvariantCulture, out var maxKeys))
        {
            await ErrorAsync(context, HttpStatusCode.BadRequest, "InvalidArgument");
            return;
        }

        maxKeys = Math.Min(maxKeys, 1000);
        var page = _bucket.Page(prefix, after, maxKeys + 1);
        var truncated = maxKeys > 0 && page.Count > maxKeys;
        page = page[..Math.Min(page.Count, maxKeys)];
        var url = parameters["encoding-type"] == "url";
        string Out(string value)
        {
            return url ? string.Join('/', value.Split('/').Select(Uri.EscapeDataString)) : value;
        }

        await XmlAsync(context, HttpStatusCode.OK, new XElement(S3 + "ListBucketResult",
            new XElement(S3 + "Name", _name),
            new XElement(S3 + "Prefix", Out(prefix)),
            startAfter is null ? null : new XElement(S3 + "StartAfter", Out(startAfter)),
            parameters["continuation-token"] is { } current ? new XElement(S3 + "ContinuationToken", current) : null,
            new XElement(S3 + "KeyCount", page.Count),
            new XElement(S3 + "MaxKeys", maxKeys),
            url ? new XElement(S3 + "EncodingType", "url") : null,
            new XElement(S3 + "IsTruncated", truncated ? "true" : "false"),
            truncated ? new XElement(S3 + "NextContinuationToken", Base64Url.EncodeToString(Encoding.UTF8.GetBytes(page[^1].Key))) : null,
            page.Select(item => new XElement(S3 + "Contents",
                new XElement(S3 + "Key", Out(item.Key)),
                new XElement(S3 + "LastModified", item.Modified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
                new XElement(S3 + "ETag", item.ETag),
                new XElement(S3 + "Size", item.Size),
                new XElement(S3 + "StorageClass", "STANDARD")))));
    }

    private async Task PutAsync(HttpContext context, string key)
    {
        var request = context.Request;
        if (request.Headers.ContainsKey("x-amz-copy-source"))
        {
            await ErrorAsync(context, HttpStatusCode.NotImplemented, "NotImplemented");
            return;
        }

        if (!SignedPayload(request, out var expected))
        {
            await ErrorAsync(context, HttpStatusCode.NotImplemented, "NotImplemented");
            return;
        }

        var failAfterStore = false;
        if (_faults is { } faults && FaultApplies(key))
        {
            var count = Interlocked.Increment(ref _faultPuts);
            if (faults.PutDelayMs > 0)
            {
                Log($"fault put_delay {key}");
                await Task.Delay(faults.PutDelayMs, context.RequestAborted);
            }

            if (faults.PutFailEvery > 0 && count % faults.PutFailEvery == 0)
            {
                Log($"fault put_fail {key}");
                // The body is read (and dropped), so the client sees a clean 500 rather than a reset connection.
                await request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
                await ErrorAsync(context, HttpStatusCode.InternalServerError, "InternalError");
                return;
            }

            failAfterStore = (faults.PutStoreThenFailEvery > 0 && count % faults.PutStoreThenFailEvery == 0)
                || (faults.PutStoreThenFailFrom > 0 && count >= faults.PutStoreThenFailFrom && count < faults.PutStoreThenFailFrom + faults.PutStoreThenFailCount);
        }

        var stored = await _bucket.PutAsync(key, request.Body, expected, context.RequestAborted, UserMetadata(request));
        if (stored is null)
        {
            await ErrorAsync(context, HttpStatusCode.BadRequest, "XAmzContentSHA256Mismatch");
            return;
        }

        if (failAfterStore)
        {
            Log($"fault put_store_then_fail {key}");
            await ErrorAsync(context, HttpStatusCode.InternalServerError, "InternalError");
            return;
        }

        context.Response.Headers.ETag = _bucket.ETag(key);
    }

    private bool FaultApplies(string key) => !key.StartsWith(CatalogAnchors, StringComparison.Ordinal) && (_faultKeys is null || _faultKeys.IsMatch(key));

    // The signed SHA-256 of a whole payload, null when unsigned; false for aws-chunked (STREAMING-*) bodies: the node signs
    // whole payloads (UseChunkEncoding = false).
    private static bool SignedPayload(HttpRequest request, out string? expected)
    {
        var signed = request.Headers["x-amz-content-sha256"].ToString();
        expected = signed.Length == 64 && signed.All(char.IsAsciiHexDigit) ? signed : null;
        return expected is not null || signed.Length == 0 || signed == "UNSIGNED-PAYLOAD";
    }

    // S3 user metadata: every x-amz-meta-* header, its name lowercased.
    private static Dictionary<string, string> UserMetadata(HttpRequest request) =>
        request.Headers.Where(header => header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key.ToLowerInvariant(), header => header.Value.ToString(), StringComparer.Ordinal);

    private static bool Has(System.Collections.Specialized.NameValueCollection parameters, string name) =>
        parameters[name] is not null || parameters.GetValues(null)?.Contains(name) == true;

    private async Task CreateUploadAsync(HttpContext context, string key)
    {
        var id = Ulid.NewUlid().ToString();
        var directory = Directory.CreateDirectory(Path.Combine(_uploads, id)).FullName;
        // The metadata first: an upload is known by its key file, so it never exists without its metadata.
        await PitrJson.WriteAtomicAsync(Path.Combine(directory, "metadata"), UserMetadata(context.Request), PitrJson.Default.DictionaryStringString, context.RequestAborted);
        await File.WriteAllTextAsync(Path.Combine(directory, "key"), key, Encoding.UTF8, context.RequestAborted);
        Log($"multipart create {key} {id}");
        await XmlAsync(context, HttpStatusCode.OK, new XElement(S3 + "InitiateMultipartUploadResult",
            new XElement(S3 + "Bucket", _name), new XElement(S3 + "Key", key), new XElement(S3 + "UploadId", id)));
    }

    // UploadPart (PUT), CompleteMultipartUpload (POST) and AbortMultipartUpload (DELETE) of an upload of this key.
    private async Task MultipartAsync(HttpContext context, string method, string key, string uploadId, string? partNumber)
    {
        // Upload ids are this server's ULIDs: anything else (a path, say) names no upload.
        var directory = Path.Combine(_uploads, uploadId);
        if (!Ulid.TryParse(uploadId, out var parsed) || parsed.ToString() != uploadId || !File.Exists(Path.Combine(directory, "key"))
            || await File.ReadAllTextAsync(Path.Combine(directory, "key"), Encoding.UTF8, context.RequestAborted) != key)
        {
            await ErrorAsync(context, HttpStatusCode.NotFound, "NoSuchUpload");
            return;
        }

        if (HttpMethods.IsPut(method))
        {
            await UploadPartAsync(context, key, directory, partNumber);
        }
        else if (HttpMethods.IsPost(method))
        {
            await CompleteAsync(context, key, directory);
        }
        else if (HttpMethods.IsDelete(method))
        {
            Directory.Delete(directory, recursive: true);
            Log($"multipart abort {key} {uploadId}");
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        }
        else
        {
            // ListParts (GET) and the rest: refused until a client needs them.
            await ErrorAsync(context, HttpStatusCode.NotImplemented, "NotImplemented");
        }
    }

    private async Task UploadPartAsync(HttpContext context, string key, string directory, string? partNumber)
    {
        var request = context.Request;
        if (!int.TryParse(partNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 10000)
        {
            await ErrorAsync(context, HttpStatusCode.BadRequest, "InvalidArgument");
            return;
        }

        if (request.Headers.ContainsKey("x-amz-copy-source") || !SignedPayload(request, out var expected))
        {
            await ErrorAsync(context, HttpStatusCode.NotImplemented, "NotImplemented");
            return;
        }

        if (_faults is { } faults && FaultApplies(key))
        {
            var count = Interlocked.Increment(ref _faultParts);
            if (faults.PutDelayMs > 0)
            {
                Log($"fault put_delay {key} part {number}");
                await Task.Delay(faults.PutDelayMs, context.RequestAborted);
            }

            if (count == faults.PartFailAt || (faults.PartFailEvery > 0 && count % faults.PartFailEvery == 0))
            {
                Log($"fault part_fail {key} part {number}");
                await request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
                await ErrorAsync(context, HttpStatusCode.InternalServerError, "InternalError");
                return;
            }
        }

        var written = await _bucket.WriteTempAsync(request.Body, expected, context.RequestAborted);
        if (written is not { } part)
        {
            await ErrorAsync(context, HttpStatusCode.BadRequest, "XAmzContentSHA256Mismatch");
            return;
        }

        try
        {
            // A retried part replaces the earlier one, as in S3.
            File.Move(part.Temp, Path.Combine(directory, number.ToString("D5", CultureInfo.InvariantCulture)), overwrite: true);
        }
        catch (DirectoryNotFoundException)
        {
            File.Delete(part.Temp);
            await ErrorAsync(context, HttpStatusCode.NotFound, "NoSuchUpload");
            return;
        }

        context.Response.Headers.ETag = $"\"{Convert.ToHexStringLower(part.Md5)}\"";
    }

    private async Task CompleteAsync(HttpContext context, string key, string directory)
    {
        if (context.Request.ContentLength is > 4 << 20)
        {
            await ErrorAsync(context, HttpStatusCode.BadRequest, "MalformedXML");
            return;
        }

        List<(int Number, string ETag)> parts;
        try
        {
            var document = await XDocument.LoadAsync(context.Request.Body, LoadOptions.None, context.RequestAborted);
            parts = [.. document.Root!.Elements().Where(element => element.Name.LocalName == "Part").Select(element => (
                int.Parse(element.Elements().Single(child => child.Name.LocalName == "PartNumber").Value, NumberStyles.None, CultureInfo.InvariantCulture),
                element.Elements().Single(child => child.Name.LocalName == "ETag").Value.Trim('"')))];
        }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidOperationException or FormatException or OverflowException or NullReferenceException)
        {
            await ErrorAsync(context, HttpStatusCode.BadRequest, "MalformedXML");
            return;
        }

        if (parts.Count == 0)
        {
            await ErrorAsync(context, HttpStatusCode.BadRequest, "MalformedXML");
            return;
        }

        if (parts.Zip(parts.Skip(1)).Any(pair => pair.First.Number >= pair.Second.Number))
        {
            await ErrorAsync(context, HttpStatusCode.BadRequest, "InvalidPartOrder");
            return;
        }

        // The listed parts, in order, into one temp file; each must exist with the listed ETag (its MD5). Unlisted parts are
        // dropped. The object's ETag is S3's: the MD5 of the listed parts' MD5s, then -N.
        var temp = _bucket.NewTemp();
        using var composite = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        string? refusal = null;
        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            foreach (var (index, (number, etag)) in parts.Index())
            {
                var path = Path.Combine(directory, number.ToString("D5", CultureInfo.InvariantCulture));
                if (!File.Exists(path))
                {
                    refusal = "InvalidPart";
                    break;
                }

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                long size = 0;
                await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1 << 16];
                    int read;
                    while ((read = await input.ReadAsync(buffer, context.RequestAborted)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                        size += read;
                    }
                }

                var md5 = hash.GetHashAndReset();
                if (Convert.ToHexStringLower(md5) != etag)
                {
                    refusal = "InvalidPart";
                    break;
                }

                composite.AppendData(md5);

                if (index < parts.Count - 1 && size < MinPartBytes)
                {
                    refusal = "EntityTooSmall";
                    break;
                }
            }

            output.Flush(flushToDisk: true);
        }

        if (refusal is not null)
        {
            File.Delete(temp);
            await ErrorAsync(context, HttpStatusCode.BadRequest, refusal);
            return;
        }

        // An upload created before metadata was kept has none.
        var metadataFile = Path.Combine(directory, "metadata");
        var metadata = File.Exists(metadataFile)
            ? System.Text.Json.JsonSerializer.Deserialize(await File.ReadAllBytesAsync(metadataFile, context.RequestAborted), PitrJson.Default.DictionaryStringString)
            : null;
        _bucket.Commit(temp, key, string.Create(CultureInfo.InvariantCulture, $"\"{Convert.ToHexStringLower(composite.GetHashAndReset())}-{parts.Count}\""), metadata);
        Directory.Delete(directory, recursive: true);
        Log($"multipart complete {key} {Path.GetFileName(directory)} parts {parts.Count}");
        await XmlAsync(context, HttpStatusCode.OK, new XElement(S3 + "CompleteMultipartUploadResult",
            new XElement(S3 + "Location", $"{Endpoint}{_name}/{key}"), new XElement(S3 + "Bucket", _name), new XElement(S3 + "Key", key),
            new XElement(S3 + "ETag", _bucket.ETag(key))));
    }

    private async Task GetAsync(HttpContext context, string key, bool head)
    {
        FileStream file;
        try
        {
            file = new FileStream(_bucket.PathOf(key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            if (head)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            await ErrorAsync(context, HttpStatusCode.NotFound, "NoSuchKey");
            return;
        }

        await using (file)
        {
            var modified = File.GetLastWriteTimeUtc(file.SafeFileHandle);
            var (etag, metadata) = _bucket.Describe(key, file.Length, modified);
            var response = context.Response;
            response.ContentType = "application/octet-stream";
            response.ContentLength = file.Length;
            response.Headers.ETag = etag;
            foreach (var (name, value) in metadata)
            {
                response.Headers[name] = value;
            }

            response.Headers.LastModified = modified.ToString("R", CultureInfo.InvariantCulture);
            if (!head)
            {
                await file.CopyToAsync(response.Body, context.RequestAborted);
            }
        }
    }

    private static Task ErrorAsync(HttpContext context, HttpStatusCode status, string code) =>
        XmlAsync(context, status, new XElement("Error", new XElement("Code", code), new XElement("Message", code)));

    private static async Task XmlAsync(HttpContext context, HttpStatusCode status, XElement body)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync(new XDocument(new XDeclaration("1.0", "UTF-8", null), body).ToString(), context.RequestAborted);
    }
}
