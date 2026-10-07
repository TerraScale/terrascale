using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Backup;

/// <summary>The master key that wraps backup data keys (envelope encryption).</summary>
internal interface IMasterKey
{
    /// <summary>A new 256-bit data key, in the clear and wrapped by the master key.</summary>
    Task<(byte[] Key, string Wrapped)> GenerateDataKeyAsync(CancellationToken cancellationToken);

    /// <summary>Unwraps a data key from <see cref="GenerateDataKeyAsync"/>.</summary>
    Task<byte[]> UnwrapAsync(string wrapped, CancellationToken cancellationToken);
}

/// <summary>Body of OCI Vault's <c>generateDataKey</c>.</summary>
/// <param name="KeyId">The master key OCID.</param>
/// <param name="IncludePlaintextKey">Whether the response carries the key in the clear.</param>
/// <param name="KeyShape">The data key to generate.</param>
public sealed record OciGenerateDataKeyRequest(string KeyId, bool IncludePlaintextKey, OciKeyShape KeyShape);

/// <summary>The shape of a generated data key.</summary>
/// <param name="Algorithm"><c>AES</c>.</param>
/// <param name="Length">Bytes; 32 for AES-256.</param>
public sealed record OciKeyShape(string Algorithm, int Length);

/// <summary>Body of OCI Vault's <c>decrypt</c>.</summary>
/// <param name="KeyId">The master key OCID.</param>
/// <param name="Ciphertext">A wrapped data key.</param>
public sealed record OciDecryptRequest(string KeyId, string Ciphertext);

/// <summary>What OCI Vault's <c>generateDataKey</c> and <c>decrypt</c> answer (the fields TerraBase reads).</summary>
/// <param name="Plaintext">The data key in the clear, base64.</param>
/// <param name="Ciphertext">The wrapped data key (<c>generateDataKey</c> only).</param>
public sealed record OciKeyResponse(string? Plaintext, string? Ciphertext);

/// <summary>
/// A master encryption key in OCI Vault, called through the vault's cryptographic endpoint with an OCI API signing key
/// (HTTP signatures, <c>rsa-sha256</c>). The OCI .NET SDK depends on the banned Newtonsoft.Json, so the two calls are signed here.
/// </summary>
internal sealed class OciVaultMasterKey(BackupSettings settings, IHttpClientFactory http, TimeProvider time) : IMasterKey, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> every request goes through.</summary>
    public const string HttpClientName = "backup-kms";

    private const int KeyBytes = 32;
    private readonly RSA _signer = CreateSigner(settings.KmsPrivateKeyPem);

    public async Task<(byte[] Key, string Wrapped)> GenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync("/20180608/generateDataKey",
            JsonSerializer.SerializeToUtf8Bytes(new OciGenerateDataKeyRequest(settings.KmsKeyId, true, new("AES", KeyBytes)), TerraBaseJsonContext.Default.OciGenerateDataKeyRequest),
            cancellationToken).ConfigureAwait(false);
        return response.Ciphertext is { Length: > 0 } wrapped ? (Key(response), wrapped) : throw new InvalidDataException("OCI Vault returned no wrapped data key");
    }

    public async Task<byte[]> UnwrapAsync(string wrapped, CancellationToken cancellationToken) =>
        Key(await SendAsync("/20180608/decrypt",
            JsonSerializer.SerializeToUtf8Bytes(new OciDecryptRequest(settings.KmsKeyId, wrapped), TerraBaseJsonContext.Default.OciDecryptRequest),
            cancellationToken).ConfigureAwait(false));

    public void Dispose() => _signer.Dispose();

    private static byte[] Key(OciKeyResponse response)
    {
        try
        {
            return Convert.FromBase64String(response.Plaintext ?? "") is { Length: KeyBytes } key
                ? key
                : throw new InvalidDataException("OCI Vault returned a data key that is not 256 bits");
        }
        catch (FormatException)
        {
            throw new InvalidDataException("OCI Vault returned a data key that is not base64");
        }
    }

    private async Task<OciKeyResponse> SendAsync(string path, byte[] body, CancellationToken cancellationToken)
    {
        var uri = new Uri(settings.KmsEndpoint, path);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var date = time.GetUtcNow().ToString("r", CultureInfo.InvariantCulture);
        var digest = Convert.ToBase64String(SHA256.HashData(body));
        request.Headers.TryAddWithoutValidation("date", date);
        request.Content.Headers.TryAddWithoutValidation("x-content-sha256", digest);
        var signed = string.Join('\n',
            $"date: {date}",
            $"(request-target): post {uri.AbsolutePath}",
            $"host: {uri.Authority}",
            $"content-length: {body.Length.ToString(CultureInfo.InvariantCulture)}",
            "content-type: application/json",
            $"x-content-sha256: {digest}");
        var signature = Convert.ToBase64String(_signer.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        request.Headers.TryAddWithoutValidation("authorization",
            $"Signature version=\"1\",headers=\"date (request-target) host content-length content-type x-content-sha256\",keyId=\"{settings.KmsApiKey}\",algorithm=\"rsa-sha256\",signature=\"{signature}\"");

        using var response = await http.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The body may echo request details; the status is enough to act on.
            throw new HttpRequestException($"OCI Vault answered {(int)response.StatusCode}", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.OciKeyResponse, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("OCI Vault returned an empty response");
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
