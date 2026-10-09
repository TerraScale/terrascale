using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Oidc;

namespace TerraScale.Auth.Features.SigningKeys;

/// <summary>One version of an instance's ring secret.</summary>
/// <param name="Content">The version's bytes.</param>
/// <param name="Version">The token a conditional write must name, or <see langword="null"/> while another write is still being applied.</param>
/// <param name="Secret">The secret's identity (its OCID); a recreated secret has a new one.</param>
/// <param name="Number">The version number; later writes to the same secret have higher numbers.</param>
/// <param name="ServerTime">The vault's clock when it answered: the authority for key activation and retirement.</param>
public sealed record VaultVersion(byte[] Content, string? Version, string Secret, long Number, DateTimeOffset ServerTime);

/// <summary>
/// Where an instance's signing key ring lives: one secret in OCI Vault, versioned by every write. The secret must
/// exist; an empty JSON object means no ring yet.
/// </summary>
public interface ISigningKeyVault
{
    /// <summary>
    /// The secret's current version. Only a <paramref name="writable"/> read returns the version token a write needs;
    /// it costs a second request. Fails with 503 when the secret cannot be read.
    /// </summary>
    Task<VaultVersion> ReadAsync(string instanceId, bool writable, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="content"/> as the new current version of <paramref name="secret"/>, the secret a writable
    /// read returned, only if it is still at <paramref name="version"/>. Returns <see langword="false"/> when another
    /// writer changed it.
    /// </summary>
    Task<bool> TryWriteAsync(string instanceId, string secret, byte[] content, string version, CancellationToken cancellationToken);

    /// <summary>
    /// Schedules deletion of the secret's deprecated versions (neither current, pending nor previous), which OCI counts
    /// against a per-secret version limit. Returns how many it scheduled.
    /// </summary>
    Task<int> PruneAsync(string instanceId, CancellationToken cancellationToken);
}

/// <summary>Without <c>TS_AUTH_SIGNING_KEY_VAULT</c> (Development only) every key operation answers 503.</summary>
public sealed class UnconfiguredSigningKeyVault : ISigningKeyVault
{
    /// <inheritdoc />
    public Task<VaultVersion> ReadAsync(string instanceId, bool writable, CancellationToken cancellationToken) => throw ApiException.Unavailable();

    /// <inheritdoc />
    public Task<bool> TryWriteAsync(string instanceId, string secret, byte[] content, string version, CancellationToken cancellationToken) => throw ApiException.Unavailable();

    /// <inheritdoc />
    public Task<int> PruneAsync(string instanceId, CancellationToken cancellationToken) => throw ApiException.Unavailable();
}

/// <summary>
/// The ring secret in the region's OCI Vault, read through the Secrets API and versioned through the Vault API with
/// the secret's ETag as <c>If-Match</c>, so of concurrent writers on one version exactly one succeeds. Requests carry
/// OCI API-key signatures (RSA-SHA256 over the documented headers). The serving instance uses the configured secret;
/// another instance <c>i</c> uses <c>&lt;secret&gt;-i</c>, which production does not provision. Every read looks the
/// secret up by name, so a recreated secret is followed at once although OCI may keep its predecessor until the
/// deletion date. Every failure other than caller cancellation answers 503.
/// </summary>
/// <remarks>
/// The OCI .NET SDK cannot run here: its region table needs named cultures, and ts-auth runs with invariant
/// globalization. These documented REST calls replace it.
/// </remarks>
public sealed class OciSigningKeyVault : ISigningKeyVault, IDisposable
{
    private readonly SigningKeyVaultOptions options;
    private readonly RSA key = RSA.Create();
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Uri secretsApi;
    private readonly Uri vaultApi;

    /// <summary>Signs with the configured API key.</summary>
    public OciSigningKeyVault(SigningKeyVaultOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        this.key.ImportFromPem(options.PrivateKeyPem);
        this.secretsApi = options.Endpoint ?? new Uri($"https://secrets.vaults.{options.Region}.oci.oraclecloud.com/");
        this.vaultApi = options.Endpoint ?? new Uri($"https://vaults.{options.Region}.oci.oraclecloud.com/");
    }

    /// <summary>The secret holding <paramref name="instanceId"/>'s ring when the serving instance uses <paramref name="secretName"/>.</summary>
    public static string SecretName(string secretName, string instanceId) =>
        instanceId == OidcIssuer.InstanceId ? secretName : $"{secretName}-{instanceId}";

    /// <inheritdoc />
    public Task<VaultVersion> ReadAsync(string instanceId, bool writable, CancellationToken cancellationToken) => Guard(async () =>
    {
        var (bundle, serverTime) = await this.CurrentAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var id = bundle["secretId"]!.GetValue<string>();
        if (!writable)
        {
            return new VaultVersion(Content(bundle), null, id, bundle["versionNumber"]!.GetValue<long>(), serverTime);
        }

        // The ETag first, then the content of the version it named: a write in between makes the ETag stale (412).
        using var current = await this.SendAsync(HttpMethod.Get, new Uri(this.vaultApi, $"20180608/secrets/{Uri.EscapeDataString(id)}"), null, null, cancellationToken).ConfigureAwait(false);
        var secret = await JsonAsync(current, cancellationToken).ConfigureAwait(false);
        var number = secret["currentVersionNumber"]!.GetValue<long>();
        using var read = await this.SendAsync(HttpMethod.Get, new Uri(this.secretsApi, $"20190301/secretbundles/{Uri.EscapeDataString(id)}?versionNumber={number}"), null, null, cancellationToken).ConfigureAwait(false);
        var content = Content(await JsonAsync(read, cancellationToken).ConfigureAwait(false));
        // The ETag names this content only once the secret is ACTIVE again after a write. OCI ETags are unquoted.
        var etag = current.Headers.TryGetValues("etag", out var values) ? values.Single() : null;
        return new VaultVersion(content, secret["lifecycleState"]?.GetValue<string>() == "ACTIVE" ? etag : null, id, number, ServerTime(read));
    }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryWriteAsync(string instanceId, string secret, byte[] content, string version, CancellationToken cancellationToken) => Guard(async () =>
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { secretContent = new { contentType = "BASE64", content = Convert.ToBase64String(content), stage = "CURRENT" } });
        using var response = await this.SendAsync(HttpMethod.Put, new Uri(this.vaultApi, $"20180608/secrets/{Uri.EscapeDataString(secret)}"), version, body, cancellationToken).ConfigureAwait(false);
        // Another version landed first (412), or a write is still being applied (409).
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return false;
        }

        _ = await JsonAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    /// <inheritdoc />
    public Task<int> PruneAsync(string instanceId, CancellationToken cancellationToken) => Guard(async () =>
    {
        var id = (await this.CurrentAsync(instanceId, cancellationToken).ConfigureAwait(false)).Bundle["secretId"]!.GetValue<string>();
        var scheduled = 0;
        string? page = null;
        do
        {
            // Every page, following opc-next-page until the listing ends.
            var query = page is null ? string.Empty : $"?page={Uri.EscapeDataString(page)}";
            using var listed = await this.SendAsync(HttpMethod.Get, new Uri(this.vaultApi, $"20180608/secrets/{Uri.EscapeDataString(id)}/versions{query}"), null, null, cancellationToken).ConfigureAwait(false);
            page = listed.Headers.TryGetValues("opc-next-page", out var next) ? next.SingleOrDefault() : null;
            foreach (var version in (await JsonAsync(listed, cancellationToken).ConfigureAwait(false)).AsArray())
            {
                var stages = version!["stages"]?.AsArray().Select(stage => stage?.GetValue<string>()).ToList() ?? [];
                if (stages.Count == 0 || stages.Any(stage => stage != "DEPRECATED") || version["timeOfDeletion"] is not null)
                {
                    continue;
                }

                // OCI's default deletion delay applies; until then the version stays readable.
                var number = version["versionNumber"]!.GetValue<long>();
                using var deleted = await this.SendAsync(HttpMethod.Post, new Uri(this.vaultApi, $"20180608/secrets/{Uri.EscapeDataString(id)}/version/{number}/actions/scheduleDeletion"), null, "{}"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                if (deleted.StatusCode == HttpStatusCode.Conflict)
                {
                    // Every replica prunes: another one scheduled this version after the listing. Quota and
                    // permission failures still fail the pass.
                    continue;
                }

                if (!deleted.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"OCI answered {(int)deleted.StatusCode}.", null, deleted.StatusCode);
                }

                scheduled++;
            }
        }
        while (!string.IsNullOrEmpty(page));

        return scheduled;
    }, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        this.http.Dispose();
        this.key.Dispose();
    }

    private static async Task<T> Guard<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not (ApiException { Status: 503 } or OutOfMemoryException)
            && !(failure is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw ApiException.Unavailable(failure);
        }
    }

    private static DateTimeOffset ServerTime(HttpResponseMessage response) =>
        response.Headers.Date ?? throw new InvalidOperationException("OCI answered without a Date header.");

    private static byte[] Content(JsonNode bundle) =>
        bundle["secretBundleContent"]?["contentType"]?.GetValue<string>() == "BASE64"
            ? Convert.FromBase64String(bundle["secretBundleContent"]!["content"]!.GetValue<string>())
            : throw new InvalidOperationException("The ring secret is not base64 content.");

    // A failure keeps only the status: OCI error bodies are not needed in logs.
    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OCI answered {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("OCI answered an empty body.");
    }

    // GetSecretBundleByName: a POST with its parameters in the query and no body, as the OCI SDKs send it.
    private async Task<(JsonNode Bundle, DateTimeOffset ServerTime)> CurrentAsync(string instanceId, CancellationToken cancellationToken)
    {
        var query = $"secretName={Uri.EscapeDataString(SecretName(this.options.SecretName, instanceId))}&vaultId={Uri.EscapeDataString(this.options.VaultId)}&stage=CURRENT";
        using var response = await this.SendAsync(HttpMethod.Post, new Uri(this.secretsApi, $"20190301/secretbundles/actions/getByName?{query}"), null, [], cancellationToken).ConfigureAwait(false);
        return (await JsonAsync(response, cancellationToken).ConfigureAwait(false), ServerTime(response));
    }

    // OCI request signing: https://docs.oracle.com/iaas/Content/API/Concepts/signingrequests.htm
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, string? ifMatch, byte[]? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        var signed = new List<(string Name, string Value)>
        {
            ("(request-target)", $"{method.Method.ToLowerInvariant()} {uri.PathAndQuery}"),
            ("date", DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture)),
            ("host", uri.Authority),
        };
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            signed.Add(("content-length", body.Length.ToString(CultureInfo.InvariantCulture)));
            signed.Add(("content-type", "application/json"));
            signed.Add(("x-content-sha256", Convert.ToBase64String(SHA256.HashData(body))));
            request.Headers.TryAddWithoutValidation("x-content-sha256", signed[^1].Value);
        }

        request.Headers.TryAddWithoutValidation("date", signed[1].Value);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("if-match", ifMatch);
        }

        var signature = this.key.SignData(Encoding.UTF8.GetBytes(string.Join('\n', signed.Select(header => $"{header.Name}: {header.Value}"))), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.Headers.TryAddWithoutValidation("authorization",
            $"Signature version=\"1\",keyId=\"{this.options.TenancyId}/{this.options.UserId}/{this.options.Fingerprint}\",algorithm=\"rsa-sha256\","
            + $"headers=\"{string.Join(' ', signed.Select(header => header.Name))}\",signature=\"{Convert.ToBase64String(signature)}\"");
        return await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
