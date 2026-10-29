using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Tests;

internal sealed class ProjectEnrollment(string directory, RegionalAnchorBucket issuer, ECDsa key, string keyId, TerraBaseTestServer server) : IAsyncDisposable
{
    internal TerraBaseTestServer Server => server;

    internal static async Task<ProjectEnrollment> StartAsync(CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("project-enrollment-").FullName;
        var trustFile = Path.Combine(directory, "issuer.pem");
        var issuer = await RegionalAnchorBucket.StartAsync(trustFile);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TerraBaseTestServer? server = null;
        try
        {
            var keyId = AuthStore.NewId();
            var publicKey = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(key) { KeyId = keyId });
            using var root = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(trustFile, cancellationToken));
            using var client = new HttpClient(new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck, CustomTrustStore = { root } },
                },
            });
            using var discovery = await client.PutAsJsonAsync(new Uri(issuer.Address, "terrascale/.well-known/openid-configuration"),
                new { issuer = new Uri(issuer.Address, "terrascale").ToString(), jwks_uri = new Uri(issuer.Address, "keys").ToString() }, cancellationToken);
            discovery.EnsureSuccessStatusCode();
            using var jwks = await client.PutAsJsonAsync(new Uri(issuer.Address, "keys"),
                new { keys = new[] { new { kty = publicKey.Kty, kid = keyId, crv = publicKey.Crv, x = publicKey.X, y = publicKey.Y, alg = "ES256", use = "sig" } } }, cancellationToken);
            jwks.EnsureSuccessStatusCode();
            var started = await TerraBaseTestServer.StartWithIssuerAsync(new Uri(issuer.Address, "terrascale").ToString(), trustFile, cancellationToken);
            server = started.Server ?? throw new InvalidOperationException(started.Unavailable);
            return new ProjectEnrollment(directory, issuer, key, keyId, server);
        }
        catch
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }
            await issuer.DisposeAsync();
            key.Dispose();
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    internal async Task EnrollAsync(string project)
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = new Uri(issuer.Address, "terrascale").ToString(),
            Audience = "https://api.terrascale.tech",
            TokenType = "at+jwt",
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key) { KeyId = keyId }, SecurityAlgorithms.EcdsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = AuthStore.NewId(),
                ["project_id"] = project,
                ["org_id"] = AuthStore.NewId(),
                ["scope"] = "databases:read databases:write",
            },
            Expires = DateTime.UtcNow.AddMinutes(5),
        });
        using var create = new HttpRequestMessage(HttpMethod.Post, new Uri(server.ClientAddress, $"v1/projects/{project}/databases"))
        {
            Content = JsonContent.Create(new { name = "enrollment" }),
        };
        create.Headers.Authorization = new("Bearer", token);
        using var created = await server.PublicHttp.SendAsync(create);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
        using var delete = new HttpRequestMessage(HttpMethod.Delete, new Uri(server.ClientAddress, $"v1/projects/{project}/databases/enrollment"));
        delete.Headers.Authorization = new("Bearer", token);
        using var deleted = await server.PublicHttp.SendAsync(delete);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await deleted.Content.ReadAsStringAsync());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await server.DisposeAsync();
        }
        finally
        {
            await issuer.DisposeAsync();
            key.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
