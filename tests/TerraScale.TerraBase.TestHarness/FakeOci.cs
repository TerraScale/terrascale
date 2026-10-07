using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace TerraBase.Testing;

/// <summary>
/// OCI Object Storage (its S3-compatible API, path-style) and an OCI Vault master key, in memory, for continuous backup.
/// <see cref="Route"/> serves both through message handlers that replace the server's transports (no socket). The vault
/// checks every request's OCI HTTP signature against the API key it generated; wrapping a data key just tags it.
/// </summary>
public sealed class FakeOci : IDisposable
{
    public const string Bucket = "backup-test";
    public const string CustomerBucket = "customer-backup";
    public const string CustomerEndpoint = "https://customer-storage.test";
    public const string CustomerAccessKey = "customer-access";
    public const string CustomerSecretKey = "customer-secret";
    private const string ObjectStorage = "https://objectstorage.test";
    private const string Vault = "https://vault-crypto.test";
    private const string VaultManagement = "https://vault-management.test";
    private const string VaultSecrets = "https://vault-secrets.test";
    private const string VaultId = "ocid1.vault.oc1.test.backup";
    private const string CompartmentId = "ocid1.compartment.oc1..backup";
    private const string KeyId = "ocid1.key.oc1.test.master";
    private const string ApiKey = "ocid1.tenancy.oc1..test/ocid1.user.oc1..test/aa:bb:cc";
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";

    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, KeyRequests> _keyRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _customerObjects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _scheduledSecrets = new(StringComparer.Ordinal);
    private readonly RSA _apiKey = RSA.Create(2048);
    private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-oci-").FullName;
    private int _badSignatures;
    private int _generatedKeys;
    private int _listRequests;
    private string? _lastListStartAfter;

    /// <summary>Objects the bucket's retention rule protects from deletion, as OCI answers <c>RetentionRuleViolation</c>.</summary>
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

    /// <summary>Customer prefixes returning empty truncated pages with alternating continuation tokens.</summary>
    public Func<string, bool>? EmptyCustomerPages { get; set; }

    /// <summary>Replaces a customer GET body; null retains the stored object's body.</summary>
    public Func<string, byte[]?>? CustomerReadOverride { get; set; }

    /// <summary>Replaces customer GET content, including a streamed body; null retains the normal body.</summary>
    public Func<string, HttpContent?>? CustomerContentOverride { get; set; }

    /// <summary>Runs before a customer PUT is accepted; gates must observe the request's cancellation token.</summary>
    public Func<string, CancellationToken, Task>? BeforeCustomerWrite { get; set; }

    /// <summary>Observes object requests without retaining an unbounded request history; true identifies the customer.</summary>
    public Action<bool, HttpMethod, string>? ObjectRequested { get; set; }

    /// <summary>Every stored object, by key.</summary>
    public IReadOnlyDictionary<string, byte[]> Objects => _objects;

    /// <summary>Customer bucket objects, separate from the platform bucket.</summary>
    public IReadOnlyDictionary<string, byte[]> CustomerObjects => _customerObjects;

    /// <summary>Decoded secret contents stored in this test's Vault, by secret OCID.</summary>
    public IReadOnlyDictionary<string, string> Secrets => _secrets;

    /// <summary>Secret OCIDs scheduled for deletion (OCI keeps their contents until deletion).</summary>
    public IReadOnlyCollection<string> ScheduledSecrets => [.. _scheduledSecrets.Keys];

    /// <summary>Vault requests whose signature did not verify; they were refused.</summary>
    public int BadSignatures => Volatile.Read(ref _badSignatures);

    /// <summary>Data keys the vault generated.</summary>
    public int GeneratedKeys => Volatile.Read(ref _generatedKeys);

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

    /// <summary>Sets every <c>backup-*</c> setting to this fake.</summary>
    public void Configure(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var secret = Path.Combine(_directory, "secret");
        var key = Path.Combine(_directory, "api-key.pem");
        File.WriteAllText(secret, "test-secret-key\n");
        File.WriteAllText(key, _apiKey.ExportPkcs8PrivateKeyPem());
        settings["backup-endpoint"] = ObjectStorage;
        settings["backup-region"] = "test-region-1";
        settings["backup-bucket"] = Bucket;
        settings["backup-access-key-id"] = "test-access-key";
        settings["backup-secret-key-file"] = secret;
        settings["backup-kms-endpoint"] = Vault;
        settings["backup-kms-key-id"] = KeyId;
        settings["backup-kms-api-key"] = ApiKey;
        settings["backup-kms-private-key-file"] = key;
    }

    /// <summary>Enables platform backup and customer credential storage in this test's Vault.</summary>
    public void ConfigureCustomer(Dictionary<string, string> settings)
    {
        Configure(settings);
        settings["backup-vault-id"] = VaultId;
        settings["backup-vault-compartment-id"] = CompartmentId;
        settings["backup-vault-endpoint"] = VaultManagement;
        settings["backup-secrets-endpoint"] = VaultSecrets;
    }

    /// <summary>Routes the server's object storage and vault clients here.</summary>
    public void Route(IServiceCollection services)
    {
        services.AddHttpClient("backup-s3").ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
        services.AddHttpClient("backup-s3-customer").ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
        services.AddHttpClient("backup-kms").ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
        services.AddHttpClient("backup-vault-secrets").ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
    }

    public void Dispose()
    {
        _apiKey.Dispose();
        Directory.Delete(_directory, recursive: true);
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

        return uri.GetLeftPart(UriPartial.Authority) switch
        {
            Vault => VaultResponse(request, body),
            VaultManagement or VaultSecrets => SecretResponse(request, body),
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

            IReadOnlyList<string> keys = [.. objects.Keys.Where(item => item.StartsWith(prefix, StringComparison.Ordinal) && string.CompareOrdinal(item, after) > 0)
                .Order(StringComparer.Ordinal).Take(1001)];
            if (customer && CustomerListingOverride is { } listing)
            {
                keys = listing(prefix, keys);
            }

            var truncated = keys.Count > 1000;
            keys = [.. keys.Take(1000)];
            var result = new XElement(S3 + "ListBucketResult",
                new XElement(S3 + "Name", bucket),
                new XElement(S3 + "Prefix", prefix),
                new XElement(S3 + "KeyCount", keys.Count),
                new XElement(S3 + "MaxKeys", 1000),
                new XElement(S3 + "IsTruncated", truncated ? "true" : "false"),
                truncated ? new XElement(S3 + "NextContinuationToken", keys[^1]) : null,
                keys.Select(item => new XElement(S3 + "Contents",
                    new XElement(S3 + "Key", item),
                    new XElement(S3 + "LastModified", "2026-10-07T00:00:00.000Z"),
                    new XElement(S3 + "ETag", ETag(objects.GetValueOrDefault(item, []))),
                    new XElement(S3 + "Size", objects.TryGetValue(item, out var bytes) ? bytes.Length : 0),
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

            if (!objects.TryGetValue(key, out var stored))
            {
                return S3Error(HttpStatusCode.NotFound, "NoSuchKey");
            }

            var get = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = (customer ? CustomerContentOverride?.Invoke(key) : null)
                    ?? new ByteArrayContent(customer ? CustomerReadOverride?.Invoke(key) ?? stored : stored),
            };
            get.Headers.ETag = new(ETag(stored));
            get.Content.Headers.LastModified = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
            return get;
        }

        if (request.Method == HttpMethod.Head)
        {
            return new HttpResponseMessage(key.Length == 0 || objects.ContainsKey(key) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
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

    private HttpResponseMessage VaultResponse(HttpRequestMessage request, byte[] body)
    {
        if (!SignatureVerifies(request, body))
        {
            Interlocked.Increment(ref _badSignatures);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        var json = JsonNode.Parse(body)!;
        if ((string?)json["keyId"] != KeyId)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        JsonObject response;
        if (request.RequestUri!.AbsolutePath == "/20180608/generateDataEncryptionKey")
        {
            Interlocked.Increment(ref _generatedKeys);
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes((int)json["keyShape"]!["length"]!));
            response = new JsonObject { ["plaintext"] = key, ["ciphertext"] = "wrapped:" + key };
        }
        else if (request.RequestUri.AbsolutePath == "/20180608/decrypt" && (string?)json["ciphertext"] is { } wrapped && wrapped.StartsWith("wrapped:", StringComparison.Ordinal))
        {
            var fingerprint = SHA256.HashData(Encoding.ASCII.GetBytes(wrapped));
            foreach (var observed in _keyRequests.Values)
            {
                if (observed.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                {
                    observed.RecordUnwrap();
                }
            }

            response = new JsonObject { ["plaintext"] = wrapped["wrapped:".Length..] };
        }
        else
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private HttpResponseMessage SecretResponse(HttpRequestMessage request, byte[] body)
    {
        if (!SignatureVerifies(request, body))
        {
            Interlocked.Increment(ref _badSignatures);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        if (uri.GetLeftPart(UriPartial.Authority) == VaultManagement && request.Method == HttpMethod.Post && path == "/20180608/secrets")
        {
            var json = JsonNode.Parse(body)!;
            if ((string?)json["vaultId"] != VaultId || (string?)json["compartmentId"] != CompartmentId || (string?)json["keyId"] != KeyId
                || (string?)json["secretContent"]?["contentType"] != "BASE64" || (string?)json["secretContent"]?["content"] is not { } content)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            var id = "ocid1.vaultsecret.oc1.test." + Guid.NewGuid().ToString("N");
            _secrets[id] = Encoding.UTF8.GetString(Convert.FromBase64String(content));
            return Json(HttpStatusCode.OK, new JsonObject { ["id"] = id, ["lifecycleState"] = "ACTIVE" });
        }

        if (uri.GetLeftPart(UriPartial.Authority) == VaultSecrets && request.Method == HttpMethod.Get
            && path.StartsWith("/20190301/secretbundles/", StringComparison.Ordinal)
            && System.Web.HttpUtility.ParseQueryString(uri.Query)["stage"] == "CURRENT")
        {
            var id = Uri.UnescapeDataString(path["/20190301/secretbundles/".Length..]);
            return _secrets.TryGetValue(id, out var content)
                ? Json(HttpStatusCode.OK, new JsonObject
                {
                    ["secretId"] = id,
                    ["secretBundleContent"] = new JsonObject { ["contentType"] = "BASE64", ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) },
                })
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (uri.GetLeftPart(UriPartial.Authority) == VaultManagement && request.Method == HttpMethod.Post
            && path.StartsWith("/20180608/secrets/", StringComparison.Ordinal) && path.EndsWith("/actions/scheduleDeletion", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/20180608/secrets/".Length..^"/actions/scheduleDeletion".Length]);
            if (!_secrets.ContainsKey(id))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            _scheduledSecrets[id] = 0;
            return Json(HttpStatusCode.OK, new JsonObject { ["id"] = id, ["lifecycleState"] = "PENDING_DELETION" });
        }

        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    // OCI HTTP signatures include the query string; GETs need no content headers.
    private bool SignatureVerifies(HttpRequestMessage request, byte[] body)
    {
        if (!request.Headers.TryGetValues("authorization", out var values)
            || values.Single() is not { } authorization || !authorization.StartsWith("Signature ", StringComparison.Ordinal))
        {
            return false;
        }

        var fields = authorization["Signature ".Length..].Split(',')
            .Select(field => field.Split('=', 2))
            .ToDictionary(field => field[0].Trim(), field => field[1].Trim('"'), StringComparer.Ordinal);
        if (fields.GetValueOrDefault("keyId") != ApiKey || fields.GetValueOrDefault("algorithm") != "rsa-sha256"
            || fields.GetValueOrDefault("headers") is not { } headers || fields.GetValueOrDefault("signature") is not { } signature)
        {
            return false;
        }

        var names = headers.Split(' ');
        var required = request.Content is null
            ? new[] { "date", "(request-target)", "host" }
            : ["date", "(request-target)", "host", "content-length", "content-type", "x-content-sha256"];
        if (!required.All(name => names.Contains(name, StringComparer.Ordinal)) || Header(request, "date") is null
            || request.Content is not null && Convert.ToBase64String(SHA256.HashData(body)) != Header(request, "x-content-sha256"))
        {
            return false;
        }

        var lines = names.Select(name => name switch
        {
            "(request-target)" => $"(request-target): {request.Method.Method.ToLowerInvariant()} {request.RequestUri!.PathAndQuery}",
            "host" => $"host: {request.RequestUri!.Authority}",
            "content-length" => $"content-length: {body.Length.ToString(CultureInfo.InvariantCulture)}",
            _ => $"{name}: {Header(request, name)}",
        });
        return _apiKey.VerifyData(Encoding.ASCII.GetBytes(string.Join('\n', lines)), Convert.FromBase64String(signature),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    // Verify AWS SigV4 with the customer's credentials, including the payload hash and signing region.
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

    // Like OCI Object Storage, a UUID: not an MD5, so the SDK does not check downloads against it.
    private static string ETag(byte[] content) => $"\"{new Guid(SHA256.HashData(content).AsSpan(0, 16))}\"";

    private static HttpResponseMessage S3Error(HttpStatusCode status, string code) =>
        Xml(status, new XElement("Error", new XElement("Code", code), new XElement("Message", code)));

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

    private sealed class Handler(FakeOci oci) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            oci.RespondAsync(request, cancellationToken);
    }
}
