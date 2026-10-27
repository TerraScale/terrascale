using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests;

public sealed class HttpVersionTests
{
    [Test]
    [Arguments("documents", HttpProtocols.Http1AndHttp2, "HTTP/2")]
    [Arguments("auth", HttpProtocols.Http1AndHttp2, "HTTP/2")]
    [Arguments("documents", HttpProtocols.Http1, "HTTP/1.1")]
    [Arguments("auth", HttpProtocols.Http1, "HTTP/1.1")]
    public async Task HttpsNegotiatesTheProtocolAndReusesTheConnection(string client, HttpProtocols protocols, string expected)
    {
        using var key = ECDsa.Create();
        var certificateRequest = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener =>
        {
            listener.Protocols = protocols;
            listener.UseHttps(certificate);
        }));
        await using var app = builder.Build();
        var received = new ConcurrentQueue<(string Protocol, string Connection, string Authorization)>();
        app.Run(async context =>
        {
            received.Enqueue((context.Request.Protocol, context.Connection.Id, context.Request.Headers.Authorization.ToString()));
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(client == "auth" ? "{\"keys\":[]}"
                : "{\"name\":\"users\",\"created_at\":\"0\",\"id\":\"01J00000000000000000000000\"}");
        });
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var handler = TerraBaseHttp.CreateHandler();
        // Trust only this test's certificate; hostname validation still applies.
        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, errors) =>
            errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors
            && presented?.GetCertHashString() == certificate.GetCertHashString();
        using var http = new HttpClient(handler);
        if (client == "auth")
        {
            var sdk = new TerraScaleAuthClient(http, new() { BaseAddress = address, InstanceSlug = "terrascale" });
            _ = await sdk.Oidc.GetJwksAsync();
            _ = await sdk.Oidc.GetJwksAsync();
        }
        else
        {
            var sdk = new TerraBaseDocumentsClient(http, new() { BaseAddress = address, ApiKey = "synthetic-token" });
            var database = sdk.Database("01J00000000000000000000000", "app");
            _ = await database.GetCollectionAsync("users");
            _ = await database.GetCollectionAsync("users");
        }

        await Assert.That(received.Count).IsEqualTo(2);
        await Assert.That(received.Select(request => request.Protocol).Distinct().Single()).IsEqualTo(expected);
        await Assert.That(received.Select(request => request.Connection).Distinct().Count()).IsEqualTo(1);
        await Assert.That(received.All(request => request.Authorization == (client == "auth" ? "" : "Bearer synthetic-token"))).IsTrue();
        await app.StopAsync();
    }
}
