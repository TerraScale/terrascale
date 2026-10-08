using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TerraBase.Testing;

/// <summary>
/// A self-signed localhost certificate for the client listener, written as PEM files to a
/// temporary directory that is deleted on dispose.
/// </summary>
public sealed class ClientTls : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-client-tls-").FullName;
    private readonly X509Certificate2 _certificate;

    /// <summary>The public certificate PEM for clients that use their own TLS trust store.</summary>
    public string CertificatePem => _certificate.ExportCertificatePem();

    /// <summary>Creates the certificate.</summary>
    public ClientTls()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(Path.Combine(_directory, "client.pem"), _certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_directory, "client.key"), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>Enables the client listener on an ephemeral port with this certificate.</summary>
    public void Apply(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings["client-port"] = "0";
        settings["client-certificate"] = Path.Combine(_directory, "client.pem");
        settings["client-private-key"] = Path.Combine(_directory, "client.key");
    }

    /// <summary>
    /// An HTTPS client for the client listener that trusts only this certificate and, when
    /// <paramref name="apiKey"/> is set, sends it as a bearer token.
    /// </summary>
    public HttpClient CreateClient(Uri clientAddress, string? apiKey = null)
    {
        // The test timeout bounds a request: serialized durable commits can take longer than any fixed limit on a busy runner.
        var client = new HttpClient(CreateHandler()) { BaseAddress = clientAddress, Timeout = Timeout.InfiniteTimeSpan };
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        }

        return client;
    }

    /// <summary>A handler that trusts only this certificate, never follows redirects and never stores cookies.</summary>
    public SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            presented is not null && presented.GetCertHash().AsSpan().SequenceEqual(_certificate.GetCertHash());
        return handler;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _certificate.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>Issues API keys through the administrative listener.</summary>
public static class ApiKeyIssuer
{
    /// <summary>Issues a key of <paramref name="organization"/> with <paramref name="scopes"/> and returns its secret.</summary>
    public static Task<string> IssueAsync(this TerraBaseTestServer server, string organization, params string[] scopes) =>
        server.IssueNarrowedAsync(organization, project: null, database: null, scopes);

    /// <summary>Issues a key of <paramref name="organization"/>, optionally narrowed, and returns its secret.</summary>
    public static async Task<string> IssueNarrowedAsync(
        this TerraBaseTestServer server, string organization, string? project, string? database, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(server);
        using var response = await server.Client.PostAsJsonAsync(
            new Uri("/admin/api-keys", UriKind.Relative),
            new { name = "test", scopes, organizationId = organization, projectId = project, databaseId = database });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IssuedKey>();
        return body!.Secret;
    }

    private sealed record IssuedKey(string Secret);
}
