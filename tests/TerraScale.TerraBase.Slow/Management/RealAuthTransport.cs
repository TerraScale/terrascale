using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

// Adapted from ts-auth's FakeOciVault: one pre-created secret, signed requests and conditional versions.
// The HTTPS listener forwards Auth's responses verbatim; it never supplies discovery, keys or tokens.
internal sealed class RealAuthTransport : IAsyncDisposable
{
    public const string VaultId = "ocid1.vault.oc1.sa-saopaulo-1.test.vault";
    public const string SecretName = "test-ts-auth-signing-key-ring";
    private const string SecretId = "ocid1.vaultsecret.oc1.sa-saopaulo-1.test.secret";
    private const string KeyId = "ocid1.tenancy.oc1..aaaatesttenancy/ocid1.user.oc1..aaaatestuser/00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff";
    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;
    private readonly RSA _apiKey = RSA.Create(2048);
    private readonly Lock _gate = new();
    private readonly List<byte[]> _versions = ["{}"u8.ToArray()];
    private readonly HttpClient _proxy = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };

    private RealAuthTransport(WebApplication app, X509Certificate2 certificate)
    {
        (_app, _certificate) = (app, certificate);
    }

    public Uri PublicOrigin { get; private set; } = null!;
    public Uri VaultOrigin { get; private set; } = null!;
    public Uri? AuthAddress { get; set; }
    public string PrivateKeyPem => _apiKey.ExportRSAPrivateKeyPem();

    public static async Task<RealAuthTransport> StartAsync(ClientTls tls, CancellationToken token)
    {
        var certificate = tls.LoadServerCertificate();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
            kestrel.Limits.MaxConcurrentConnections = 16;
            kestrel.Listen(IPAddress.Loopback, 0);
            kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate));
        });
        var fixture = new RealAuthTransport(builder.Build(), certificate);
        fixture._app.Run(fixture.RespondAsync);
        try
        {
            await fixture._app.StartAsync(token);
            var addresses = fixture._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            fixture.PublicOrigin = new UriBuilder(addresses.Single(address => address.StartsWith("https:", StringComparison.Ordinal))) { Host = "localhost" }.Uri;
            fixture.VaultOrigin = new Uri(addresses.Single(address => address.StartsWith("http:", StringComparison.Ordinal)));
            return fixture;
        }
        catch (Exception startupError)
        {
            try
            {
                await fixture.DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(startupError, cleanupError);
            }

            throw;
        }
    }

    private async Task RespondAsync(HttpContext context)
    {
        if (context.Request.IsHttps)
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri(AuthAddress!, context.Request.Path + context.Request.QueryString));
            if (context.Request.ContentLength is > 0)
            {
                request.Content = new StreamContent(context.Request.Body);
                request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }

            request.Headers.TryAddWithoutValidation("Authorization", [.. context.Request.Headers.Authorization]);
            using var response = await _proxy.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            return;
        }

        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        byte[] answer;
        lock (_gate)
        {
            answer = VaultAnswer(context, body.ToArray());
        }

        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(answer, context.RequestAborted);
    }

    private byte[] VaultAnswer(HttpContext context, byte[] body)
    {
        var request = context.Request;
        if (!Signed(request, body))
        {
            context.Response.StatusCode = 401;
            return "{}"u8.ToArray();
        }

        var path = request.Path.Value;
        if (request.Method == "POST" && path == "/20190301/secretbundles/actions/getByName"
            && request.Query["vaultId"] == VaultId && request.Query["secretName"] == SecretName && request.Query["stage"] == "CURRENT")
        {
            return Bundle(_versions.Count);
        }

        if (request.Method == "GET" && path == "/20190301/secretbundles/" + SecretId
            && int.TryParse(request.Query["versionNumber"], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number >= 1 && number <= _versions.Count)
        {
            return Bundle(number);
        }

        if (path == "/20180608/secrets/" + SecretId)
        {
            if (request.Method == "PUT")
            {
                if (request.Headers.IfMatch != _versions.Count.ToString(CultureInfo.InvariantCulture))
                {
                    context.Response.StatusCode = 412;
                    return "{}"u8.ToArray();
                }

                // shortcut: one issuer's bootstrap only, extend the Vault fixture when testing rotation/pruning here.
                if (_versions.Count >= 8)
                {
                    context.Response.StatusCode = 400;
                    return "{}"u8.ToArray();
                }

                using var json = JsonDocument.Parse(body);
                var content = json.RootElement.GetProperty("secretContent");
                if (content.GetProperty("contentType").GetString() != "BASE64" || content.GetProperty("stage").GetString() != "CURRENT")
                {
                    context.Response.StatusCode = 400;
                    return "{}"u8.ToArray();
                }

                _versions.Add(Convert.FromBase64String(content.GetProperty("content").GetString()!));
            }
            else if (request.Method != "GET")
            {
                context.Response.StatusCode = 405;
                return "{}"u8.ToArray();
            }

            context.Response.Headers.ETag = _versions.Count.ToString(CultureInfo.InvariantCulture);
            return JsonSerializer.SerializeToUtf8Bytes(new { id = SecretId, currentVersionNumber = _versions.Count, lifecycleState = "ACTIVE" });
        }

        if (request.Method == "GET" && path == "/20180608/secrets/" + SecretId + "/versions")
        {
            return JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(1, _versions.Count).Select(version => new
            {
                secretId = SecretId,
                versionNumber = version,
                stages = new[] { version == _versions.Count ? "CURRENT" : "PREVIOUS" },
            }));
        }

        context.Response.StatusCode = 404;
        return "{}"u8.ToArray();
    }

    private byte[] Bundle(int number) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        secretId = SecretId,
        versionNumber = number,
        secretBundleContent = new { contentType = "BASE64", content = Convert.ToBase64String(_versions[number - 1]) },
    });

    private bool Signed(HttpRequest request, byte[] body)
    {
        var authorization = request.Headers.Authorization.ToString();
        var fields = authorization.StartsWith("Signature ", StringComparison.Ordinal)
            ? authorization["Signature ".Length..].Split(',').Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1].Trim('"'), StringComparer.Ordinal)
            : [];
        string[] required = request.Method == "GET"
            ? ["(request-target)", "date", "host"]
            : ["(request-target)", "date", "host", "content-length", "content-type", "x-content-sha256"];
        if (fields.GetValueOrDefault("version") != "1" || fields.GetValueOrDefault("algorithm") != "rsa-sha256"
            || fields.GetValueOrDefault("keyId") != KeyId || fields.GetValueOrDefault("headers") != string.Join(' ', required)
            || fields.GetValueOrDefault("signature") is not { } signature
            || request.Method != "GET" && (request.Headers["x-content-sha256"] != Convert.ToBase64String(SHA256.HashData(body)) || request.ContentLength != body.Length))
        {
            return false;
        }

        string Value(string header)
        {
            return header switch
            {
                "(request-target)" => $"{request.Method.ToLowerInvariant()} {request.Path}{request.QueryString}",
                "host" => request.Host.Value!,
                _ => request.Headers[header].ToString(),
            };
        }
        return _apiKey.VerifyData(Encoding.UTF8.GetBytes(string.Join('\n', required.Select(header => $"{header}: {Value(header)}"))),
            Convert.FromBase64String(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.DisposeAsync();
        }
        finally
        {
            _proxy.Dispose();
            _apiKey.Dispose();
            _certificate.Dispose();
        }
    }
}
