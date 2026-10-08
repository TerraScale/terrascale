using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Backup;

/// <summary>Customer backup credentials stored independently of backup data keys in OCI Vault.</summary>
internal interface IOciVaultSecrets
{
    /// <summary>Creates a credential secret and returns its OCID.</summary>
    Task<string> CreateAsync(string name, string accessKeyId, string secretKey, CancellationToken cancellationToken);

    /// <summary>Reads the credentials from the secret's CURRENT version.</summary>
    Task<(string AccessKeyId, string SecretKey)> ReadAsync(string secretId, CancellationToken cancellationToken);

    /// <summary>Schedules retirement of an unused secret after OCI's minimum seven-day deletion window.</summary>
    Task DeleteAsync(string secretId, CancellationToken cancellationToken);
}

/// <summary>Versioned JSON stored as the content of a customer backup credential secret.</summary>
/// <param name="Version">Format version; currently 1.</param>
/// <param name="AccessKeyId">The customer's S3 access key id.</param>
/// <param name="SecretKey">The customer's S3 secret key.</param>
public sealed record OciSecretCredentials(int Version, string? AccessKeyId, string? SecretKey)
{
    /// <summary>Omits credentials when the record is formatted.</summary>
    public override string ToString() => "backup credentials (redacted)";
}

/// <summary>Base64 secret content used by OCI's create-secret and secret-bundle APIs.</summary>
/// <param name="ContentType">The encoding discriminator, BASE64.</param>
/// <param name="Content">The base64-encoded credential JSON.</param>
/// <param name="Stage">CURRENT when creating a secret; absent from retrieved content.</param>
public sealed record OciSecretContent(string? ContentType, string? Content, string? Stage = null)
{
    /// <summary>Omits encoded credentials when the record is formatted.</summary>
    public override string ToString() => "secret content (redacted)";
}

/// <summary>Body of OCI Vault's create-secret API.</summary>
/// <param name="CompartmentId">The compartment OCID.</param>
/// <param name="VaultId">The vault OCID.</param>
/// <param name="KeyId">The symmetric encryption key OCID in that vault.</param>
/// <param name="SecretName">The secret's unique name within the vault.</param>
/// <param name="SecretContent">The encoded credentials.</param>
public sealed record OciCreateSecretRequest(string CompartmentId, string VaultId, string KeyId, string SecretName, OciSecretContent SecretContent);

/// <summary>The fields TerraBase reads from OCI's create-secret response.</summary>
/// <param name="Id">The newly created secret's OCID.</param>
public sealed record OciSecretResponse(string? Id);

/// <summary>The fields TerraBase reads from OCI's secret-bundle response.</summary>
/// <param name="SecretBundleContent">The encoded credentials.</param>
public sealed record OciSecretBundleResponse(OciSecretContent? SecretBundleContent);

/// <summary>Body of OCI Vault's schedule-secret-deletion API.</summary>
/// <param name="TimeOfDeletion">The UTC deletion time, at least seven days in the future.</param>
public sealed record OciScheduleSecretDeletionRequest(DateTimeOffset TimeOfDeletion);

/// <summary>
/// Signed HTTP calls to OCI Vault's secret management and retrieval endpoints. The OCI .NET SDK depends on
/// Newtonsoft.Json, so this uses the same API signing credentials as the backup master key.
/// </summary>
internal sealed class OciVaultSecrets(BackupSettings settings, IHttpClientFactory http, TimeProvider time) : IOciVaultSecrets, IDisposable
{
    /// <summary>The named client used for both OCI secret endpoints.</summary>
    public const string HttpClientName = "backup-vault-secrets";

    private const int MaxBodyBytes = 64 * 1024;
    private const int MaxSecretBytes = 25 * 1024;
    private const int MaxCredentialBytes = 4096;
    private const int MaxRequests = 8;

    private readonly string _vaultId = settings.VaultId ?? throw new InvalidOperationException("backup-vault-id must be set");
    private readonly string _compartmentId = settings.VaultCompartmentId ?? throw new InvalidOperationException("backup-vault-compartment-id must be set");
    private readonly Uri _vaultEndpoint = settings.VaultEndpoint ?? throw new InvalidOperationException("backup-vault-endpoint must be set");
    private readonly Uri _secretsEndpoint = settings.SecretsEndpoint ?? throw new InvalidOperationException("backup-secrets-endpoint must be set");
    private readonly RSA _signer = CreateSigner(settings.KmsPrivateKeyPem);
    // Admission happens before serializing, reading or decoding; excess callers never form an unbounded queue.
    private readonly SemaphoreSlim _requests = new(MaxRequests, MaxRequests);

    public async Task<string> CreateAsync(string name, string accessKeyId, string secretKey, CancellationToken cancellationToken)
    {
        if (!IsName(name, 256))
        {
            throw new ArgumentException("Secret name must be 1 to 256 ASCII letters, digits, dots, underscores or hyphens", nameof(name));
        }
        if (!IsCredential(accessKeyId) || !IsCredential(secretKey))
        {
            throw new ArgumentException("Backup credential fields must each be 1 to 4096 UTF-8 bytes without control characters");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = timeout.Token;
        await AdmitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var credentials = JsonSerializer.SerializeToUtf8Bytes(new OciSecretCredentials(1, accessKeyId, secretKey), Metadata<OciSecretCredentials>());
            byte[] body;
            try
            {
                if (credentials.Length > MaxSecretBytes)
                {
                    throw new ArgumentException("Backup credential JSON exceeds OCI's secret content limit");
                }
                body = JsonSerializer.SerializeToUtf8Bytes(new OciCreateSecretRequest(_compartmentId, _vaultId, settings.KmsKeyId, name,
                    new("BASE64", Convert.ToBase64String(credentials), "CURRENT")), Metadata<OciCreateSecretRequest>());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(credentials);
            }

            using var response = await SendAsync(HttpMethod.Post, new Uri(_vaultEndpoint, "/20180608/secrets"), body, cancellationToken).ConfigureAwait(false);
            var secret = await ReadResponseAsync<OciSecretResponse>(response, cancellationToken).ConfigureAwait(false);
            // A completed create must hand its id to the caller, even if cancellation now prevents publishing it.
            return IsName(secret.Id, 512) ? secret.Id! : throw new HttpRequestException("OCI Vault Secrets returned no valid secret id", null, HttpStatusCode.BadGateway);
        }
        finally
        {
            _requests.Release();
        }
    }

    public async Task<(string AccessKeyId, string SecretKey)> ReadAsync(string secretId, CancellationToken cancellationToken)
    {
        ValidateSecretId(secretId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = timeout.Token;
        await AdmitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var uri = new Uri(_secretsEndpoint, $"/20190301/secretbundles/{Uri.EscapeDataString(secretId)}?stage=CURRENT");
            using var response = await SendAsync(HttpMethod.Get, uri, null, cancellationToken).ConfigureAwait(false);
            var bundle = await ReadResponseAsync<OciSecretBundleResponse>(response, cancellationToken).ConfigureAwait(false);
            if (bundle.SecretBundleContent is not { ContentType: "BASE64", Content: { Length: > 0 } content }
                || content.Length > 4 * ((MaxSecretBytes + 2) / 3))
            {
                throw new HttpRequestException("OCI Vault Secrets returned invalid secret content", null, HttpStatusCode.BadGateway);
            }

            var decoded = new byte[MaxSecretBytes];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Convert.TryFromBase64String(content, decoded, out var length))
                {
                    throw new HttpRequestException("OCI Vault Secrets returned invalid base64 secret content", null, HttpStatusCode.BadGateway);
                }
                var credentials = Deserialize<OciSecretCredentials>(decoded.AsSpan(0, length));
                if (credentials is not { Version: 1, AccessKeyId: { } accessKeyId, SecretKey: { } secretKey }
                    || !IsCredential(accessKeyId) || !IsCredential(secretKey))
                {
                    throw new HttpRequestException("OCI Vault Secrets returned an unsupported or invalid backup credential record", null, HttpStatusCode.BadGateway);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return (accessKeyId, secretKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decoded);
            }
        }
        finally
        {
            _requests.Release();
        }
    }

    public async Task DeleteAsync(string secretId, CancellationToken cancellationToken)
    {
        ValidateSecretId(secretId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = timeout.Token;
        await AdmitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // One extra minute leaves room for the request to reach OCI after its minimum seven-day window begins.
            var body = JsonSerializer.SerializeToUtf8Bytes(new OciScheduleSecretDeletionRequest(time.GetUtcNow().AddDays(7).AddMinutes(1)), Metadata<OciScheduleSecretDeletionRequest>());
            var uri = new Uri(_vaultEndpoint, $"/20180608/secrets/{Uri.EscapeDataString(secretId)}/actions/scheduleDeletion");
            using var response = await SendAsync(HttpMethod.Post, uri, body, cancellationToken, allowMissing: true).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _requests.Release();
        }
    }

    public void Dispose()
    {
        _signer.Dispose();
        _requests.Dispose();
    }

    private async Task AdmitAsync(CancellationToken cancellationToken)
    {
        if (!await _requests.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new HttpRequestException("OCI Vault Secrets request limit reached", null, HttpStatusCode.ServiceUnavailable);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, byte[]? body, CancellationToken cancellationToken, bool allowMissing = false)
    {
        try
        {
            if (body is { Length: > MaxBodyBytes })
            {
                throw new HttpRequestException("OCI Vault Secrets request exceeds its byte limit", null, HttpStatusCode.BadGateway);
            }
            using var request = new HttpRequestMessage(method, uri);
            Sign(request, body);
            var response = await http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || (allowMissing && response.StatusCode == HttpStatusCode.NotFound))
            {
                return response;
            }
            var status = response.StatusCode;
            response.Dispose();
            // Error bodies may echo submitted content. Never read them or attach them to an exception.
            throw new HttpRequestException($"OCI Vault Secrets answered {(int)status}", null, status);
        }
        finally
        {
            if (body is not null)
            {
                CryptographicOperations.ZeroMemory(body);
            }
        }
    }

    private void Sign(HttpRequestMessage request, byte[]? body)
    {
        var uri = request.RequestUri!;
        var date = time.GetUtcNow().ToString("r", CultureInfo.InvariantCulture);
        request.Headers.TryAddWithoutValidation("date", date);
        request.Headers.Host = uri.Authority;
        var headers = "date (request-target) host";
        var signed = $"date: {date}\n(request-target): {request.Method.Method.ToLowerInvariant()} {uri.PathAndQuery}\nhost: {uri.Authority}";
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content.Headers.ContentLength = body.Length;
            var digest = Convert.ToBase64String(SHA256.HashData(body));
            request.Content.Headers.TryAddWithoutValidation("x-content-sha256", digest);
            headers += " content-length content-type x-content-sha256";
            signed += $"\ncontent-length: {body.Length.ToString(CultureInfo.InvariantCulture)}\ncontent-type: application/json\nx-content-sha256: {digest}";
        }

        string signature;
        lock (_signer)
        {
            signature = Convert.ToBase64String(_signer.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        request.Headers.TryAddWithoutValidation("authorization",
            $"Signature version=\"1\",headers=\"{headers}\",keyId=\"{settings.KmsApiKey}\",algorithm=\"rsa-sha256\",signature=\"{signature}\"");
    }

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxBodyBytes)
        {
            throw new HttpRequestException("OCI Vault Secrets response exceeds its byte limit", null, HttpStatusCode.BadGateway);
        }
        var buffer = new byte[MaxBodyBytes + 1];
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            return length <= MaxBodyBytes
                ? Deserialize<T>(buffer.AsSpan(0, length))
                : throw new HttpRequestException("OCI Vault Secrets response exceeds its byte limit", null, HttpStatusCode.BadGateway);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, Metadata<T>()) ?? throw new HttpRequestException("OCI Vault Secrets returned an empty response", null, HttpStatusCode.BadGateway);
        }
        catch (JsonException)
        {
            // JSON diagnostics can contain attacker-supplied property names, including credentials.
            throw new HttpRequestException("OCI Vault Secrets returned invalid JSON", null, HttpStatusCode.BadGateway);
        }
    }

    private static JsonTypeInfo<T> Metadata<T>() => TerraBaseJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException("OCI Vault Secrets JSON metadata must be registered");

    private static bool IsCredential(string? value) => value is { Length: > 0 and <= MaxCredentialBytes }
        && !value.Any(char.IsControl) && Encoding.UTF8.GetByteCount(value) <= MaxCredentialBytes;

    private static bool IsName(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && value is not "." and not ".."
        && value.All(character => character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-');

    private static void ValidateSecretId(string secretId)
    {
        if (!IsName(secretId, 512))
        {
            throw new ArgumentException("Secret id must be 1 to 512 ASCII letters, digits, dots, underscores or hyphens", nameof(secretId));
        }
    }

    private static RSA CreateSigner(string pem)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }
}
