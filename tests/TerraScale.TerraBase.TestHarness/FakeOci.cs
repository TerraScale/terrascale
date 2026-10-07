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
    private const string ObjectStorage = "https://objectstorage.test";
    private const string Vault = "https://vault-crypto.test";
    private const string KeyId = "ocid1.key.oc1.test.master";
    private const string ApiKey = "ocid1.tenancy.oc1..test/ocid1.user.oc1..test/aa:bb:cc";
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";

    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly RSA _apiKey = RSA.Create(2048);
    private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-oci-").FullName;
    private int _badSignatures;
    private int _generatedKeys;

    /// <summary>Objects the bucket's retention rule protects from deletion, as OCI answers <c>RetentionRuleViolation</c>.</summary>
    public Func<string, bool> Locked { get; set; } = _ => false;

    /// <summary>Every stored object, by key.</summary>
    public IReadOnlyDictionary<string, byte[]> Objects => _objects;

    /// <summary>Vault requests whose signature did not verify; they were refused.</summary>
    public int BadSignatures => Volatile.Read(ref _badSignatures);

    /// <summary>Data keys the vault generated.</summary>
    public int GeneratedKeys => Volatile.Read(ref _generatedKeys);

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

    /// <summary>Routes the server's object storage and vault clients here.</summary>
    public void Route(IServiceCollection services)
    {
        services.AddHttpClient("backup-s3").ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
        services.AddHttpClient("backup-kms").ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
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
        return uri.GetLeftPart(UriPartial.Authority) == Vault ? VaultResponse(request, body) : ObjectResponse(request, body);
    }

    private HttpResponseMessage ObjectResponse(HttpRequestMessage request, byte[] body)
    {
        var uri = request.RequestUri!;
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        var (bucket, key) = slash < 0 ? (path, "") : (path[..slash], path[(slash + 1)..]);
        if (bucket != Bucket)
        {
            return S3Error(HttpStatusCode.NotFound, "NoSuchBucket");
        }

        if (key.Length == 0 && request.Method == HttpMethod.Get)
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var prefix = query["prefix"] ?? "";
            var after = query["start-after"] ?? "";
            var keys = _objects.Keys.Where(item => item.StartsWith(prefix, StringComparison.Ordinal) && string.CompareOrdinal(item, after) > 0)
                .Order(StringComparer.Ordinal).ToList();
            var result = new XElement(S3 + "ListBucketResult",
                new XElement(S3 + "Name", Bucket),
                new XElement(S3 + "Prefix", prefix),
                new XElement(S3 + "KeyCount", keys.Count),
                new XElement(S3 + "MaxKeys", 1000),
                new XElement(S3 + "IsTruncated", "false"),
                keys.Select(item => new XElement(S3 + "Contents",
                    new XElement(S3 + "Key", item),
                    new XElement(S3 + "LastModified", "2026-10-07T00:00:00.000Z"),
                    new XElement(S3 + "ETag", ETag(_objects.GetValueOrDefault(item, []))),
                    new XElement(S3 + "Size", _objects.TryGetValue(item, out var bytes) ? bytes.Length : 0),
                    new XElement(S3 + "StorageClass", "STANDARD"))));
            return Xml(HttpStatusCode.OK, result);
        }

        if (request.Method == HttpMethod.Put)
        {
            _objects[key] = body;
            var put = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            put.Headers.ETag = new(ETag(body));
            return put;
        }

        if (request.Method == HttpMethod.Get)
        {
            if (!_objects.TryGetValue(key, out var stored))
            {
                return S3Error(HttpStatusCode.NotFound, "NoSuchKey");
            }

            var get = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stored) };
            get.Headers.ETag = new(ETag(stored));
            get.Content.Headers.LastModified = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
            return get;
        }

        if (request.Method == HttpMethod.Delete && _objects.ContainsKey(key) && Locked(key))
        {
            return S3Error(HttpStatusCode.Forbidden, "RetentionRuleViolation");
        }

        if (request.Method == HttpMethod.Delete)
        {
            // Like S3: deleting a missing key succeeds too.
            _objects.TryRemove(key, out _);
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
            response = new JsonObject { ["plaintext"] = wrapped["wrapped:".Length..] };
        }
        else
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    // OCI HTTP signatures: the listed headers, in order, signed with the API key (rsa-sha256).
    private bool SignatureVerifies(HttpRequestMessage request, byte[] body)
    {
        if (!request.Headers.TryGetValues("authorization", out var values)
            || values.Single() is not { } authorization || !authorization.StartsWith("Signature ", StringComparison.Ordinal))
        {
            return false;
        }

        var fields = authorization["Signature ".Length..].Split(',')
            .Select(field => field.Split('=', 2))
            .ToDictionary(field => field[0], field => field[1].Trim('"'), StringComparer.Ordinal);
        if (fields.GetValueOrDefault("keyId") != ApiKey || fields.GetValueOrDefault("algorithm") != "rsa-sha256"
            || Convert.ToBase64String(SHA256.HashData(body)) != Header(request, "x-content-sha256"))
        {
            return false;
        }

        var lines = fields["headers"].Split(' ').Select(name => name switch
        {
            "(request-target)" => $"(request-target): {request.Method.Method.ToLowerInvariant()} {request.RequestUri!.AbsolutePath}",
            "host" => $"host: {request.RequestUri!.Authority}",
            "content-length" => $"content-length: {body.Length.ToString(CultureInfo.InvariantCulture)}",
            _ => $"{name}: {Header(request, name)}",
        });
        return _apiKey.VerifyData(Encoding.ASCII.GetBytes(string.Join('\n', lines)), Convert.FromBase64String(fields["signature"]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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

    private sealed class Handler(FakeOci oci) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            oci.RespondAsync(request, cancellationToken);
    }
}
