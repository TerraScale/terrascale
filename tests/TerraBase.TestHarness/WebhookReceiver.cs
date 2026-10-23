using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Features.Webhooks;

namespace TerraBase.Testing;

// Keep the production transport: replace only DNS and the validated-IP socket connector.
internal sealed class WebhookReceiver : IWebhookNetwork, IAsyncDisposable
{
    private const string Host = "webhook-receiver.test";
    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;
    private readonly Channel<WebhookRequest> _requests = Channel.CreateBounded<WebhookRequest>(16);
    private IPAddress[] _addresses = [IPAddress.Parse("93.184.216.34")];
    private int _connections;
    private int _received;
    private int _redirectsFollowed;
    private int _port;

    private WebhookReceiver(WebApplication app, X509Certificate2 certificate)
    {
        _app = app;
        _certificate = certificate;
    }

    public static string Url => "https://" + Host + "/events";
    public const string ResponseMarker = "receiver-body-must-not-be-recorded";
    public int FirstStatus { get; set; } = StatusCodes.Status204NoContent;
    public int SubsequentStatus { get; set; } = StatusCodes.Status204NoContent;
    public int Connections => Volatile.Read(ref _connections);
    public int Received => Volatile.Read(ref _received);
    public int RedirectsFollowed => Volatile.Read(ref _redirectsFollowed);
    public bool UsesProxy { get; private set; } = true;
    public bool FollowsRedirects { get; private set; } = true;

    public static async Task<WebhookReceiver> StartAsync(CancellationToken token)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + Host, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Host);
        request.CertificateExtensions.Add(names.Build());
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
            kestrel.Limits.MaxConcurrentConnections = 4;
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificate);
            });
        });
        var app = builder.Build();
        var receiver = new WebhookReceiver(app, certificate);
        app.Run(receiver.RespondAsync);
        try
        {
            await app.StartAsync(token);
            receiver._port = new Uri(app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
            return receiver;
        }
        catch
        {
            await receiver.DisposeAsync();
            throw;
        }
    }

    public void Configure(IServiceCollection services)
    {
        services.AddSingleton<IWebhookNetwork>(this);
        services.AddHttpClient("TerraBase.Webhooks").ConfigurePrimaryHttpMessageHandler((handler, _) =>
        {
            var sockets = (SocketsHttpHandler)handler;
            UsesProxy = sockets.UseProxy;
            FollowsRedirects = sockets.AllowAutoRedirect;
            sockets.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
                presented is not null && presented.GetCertHash().AsSpan().SequenceEqual(_certificate.GetCertHash());
        });
    }

    public void Addresses(params string[] addresses) =>
        Volatile.Write(ref _addresses, [.. addresses.Select(IPAddress.Parse)]);

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host != Host && !IPAddress.TryParse(host, out _))
        {
            throw new HttpRequestException("unexpected fixture DNS host");
        }

        return Task.FromResult(IPAddress.TryParse(host, out var literal) ? new[] { literal } : [.. Volatile.Read(ref _addresses)]);
    }

    public async ValueTask<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connections);
        if (port != 443 || !addresses.SequenceEqual(Volatile.Read(ref _addresses)))
        {
            throw new HttpRequestException("unexpected fixture destination");
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, _port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public ValueTask<WebhookRequest> ReadAsync(CancellationToken token) => _requests.Reader.ReadAsync(token);

    private async Task RespondAsync(HttpContext context)
    {
        var count = Interlocked.Increment(ref _received);
        if (context.Request.Path == "/redirected")
        {
            Interlocked.Increment(ref _redirectsFollowed);
        }

        if (count > 16)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        await _requests.Writer.WriteAsync(new WebhookRequest(body.ToArray(), context.Request.Method,
            context.Request.ContentType, context.Request.Headers["X-TerraScale-Delivery"].ToString(),
            context.Request.Headers["X-TerraScale-Timestamp"].ToString(),
            context.Request.Headers["X-TerraScale-Signature"].ToString()), context.RequestAborted);
        context.Response.StatusCode = count == 1 ? FirstStatus : SubsequentStatus;
        // Close each socket so every attempt exercises production DNS validation again.
        context.Response.Headers.Connection = "close";
        if (context.Response.StatusCode is >= 300 and < 400)
        {
            context.Response.Headers.Location = "https://" + Host + "/redirected";
        }

        if (context.Response.StatusCode != StatusCodes.Status204NoContent)
        {
            await context.Response.WriteAsync(ResponseMarker, context.RequestAborted);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _requests.Writer.TryComplete();
        await _app.DisposeAsync();
        _certificate.Dispose();
    }
}

internal sealed record WebhookRequest(byte[] Body, string Method, string? ContentType, string Delivery, string Timestamp, string Signature);
