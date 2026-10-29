using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

// Only the socket and DNS adapters are replaced: the customer's real handler still decides whether to connect.
internal sealed class CustomerBackupNetworkFixture : ICustomerBackupDns, ICustomerBackupConnector, IAsyncDisposable
{
    private const string Host = "customer-storage.test";
    private readonly X509Certificate2 _certificate;
    private readonly WebApplication _app;
    private readonly TaskCompletionSource _resolveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _connectRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CountingProxy _proxy = new();
    private IPAddress[] _addresses = [IPAddress.Parse("93.184.216.34")];
    private byte[]? _probe;
    private int _resolutions;
    private int _connections;
    private int _requests;
    private int _puts;
    private int _gets;
    private int _lists;
    private int _deletes;
    private int _redirectsFollowed;

    private CustomerBackupNetworkFixture(WebApplication app, X509Certificate2 certificate)
    {
        _app = app;
        _certificate = certificate;
    }

    public Uri Endpoint { get; private set; } = null!;
    public bool HoldResolution { get; set; }
    public bool HoldConnect { get; set; }
    public bool Redirect { get; set; }
    public int Resolutions => Volatile.Read(ref _resolutions);
    public int Connections => Volatile.Read(ref _connections);
    public bool ProductionUsesProxy { get; private set; } = true;
    public int ProxyRoutingLookups => _proxy.RoutingLookups;
    public int Puts => Volatile.Read(ref _puts);
    public int Gets => Volatile.Read(ref _gets);
    public int Lists => Volatile.Read(ref _lists);
    public int Deletes => Volatile.Read(ref _deletes);
    public int RedirectsFollowed => Volatile.Read(ref _redirectsFollowed);
    public TaskCompletionSource Resolving { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Connecting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ConnectCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task<CustomerBackupNetworkFixture> StartAsync(CancellationToken cancellationToken)
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
            kestrel.Limits.MaxRequestBodySize = 1024;
            kestrel.Limits.MaxConcurrentConnections = 4;
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificate);
            });
        });
        var app = builder.Build();
        var fixture = new CustomerBackupNetworkFixture(app, certificate);
        app.Run(fixture.RespondAsync);
        try
        {
            await app.StartAsync(cancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            fixture.Endpoint = new UriBuilder(address) { Host = Host }.Uri;
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public void Configure(FakeS3 s3, IServiceCollection services, bool enableProxy = false)
    {
        s3.RoutePlatform(services);
        ManualBackupHosting.Configure(services);
        services.AddSingleton<ICustomerBackupDns>(this);
        services.AddSingleton<ICustomerBackupConnector>(this);
        services.AddHttpClient(S3ObjectStore.CustomerHttpClientName).ConfigurePrimaryHttpMessageHandler((handler, _) =>
        {
            var sockets = (SocketsHttpHandler)handler;
            ProductionUsesProxy = sockets.UseProxy;
            if (enableProxy)
            {
                sockets.UseProxy = true;
            }

            // Proxy routing targets a host our DNS adapter refuses, without changing process-wide proxy settings.
            sockets.Proxy = _proxy;
            sockets.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
                presented is not null && presented.GetCertHash().AsSpan().SequenceEqual(_certificate.GetCertHash());
        });
    }

    public void Addresses(params string[] values)
    {
        if (values.Length > 64)
        {
            throw new ArgumentException("fixture addresses exceed the connection bound", nameof(values));
        }

        Volatile.Write(ref _addresses, [.. values.Select(IPAddress.Parse)]);
    }

    public void ReleaseResolution() => _resolveRelease.TrySetResult();
    public void ReleaseConnect() => _connectRelease.TrySetResult();

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _resolutions) > 32 || host != Host)
        {
            throw new HttpRequestException("fixture refused an unexpected DNS lookup");
        }

        Resolving.TrySetResult();
        if (HoldResolution)
        {
            await _resolveRelease.Task.WaitAsync(cancellationToken);
        }

        return [.. Volatile.Read(ref _addresses)];
    }

    public async ValueTask<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _connections) > 32 || port != Endpoint.Port
            || !addresses.SequenceEqual(Volatile.Read(ref _addresses)))
        {
            throw new HttpRequestException("fixture refused an unexpected connection");
        }

        Connecting.TrySetResult();
        if (HoldConnect)
        {
            try
            {
                await _connectRelease.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ConnectCanceled.TrySetResult();
                throw;
            }
        }

        // Production has checked the entire answer. Route that approved connection to this test's loopback listener.
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, Endpoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task RespondAsync(HttpContext context)
    {
        // One small probe is the only object retained; counters replace a request history.
        if (Interlocked.Increment(ref _requests) > 64)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (context.Request.Path == "/redirected")
        {
            Interlocked.Increment(ref _redirectsFollowed);
        }

        if (Redirect && context.Request.Path != "/redirected")
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = new Uri(Endpoint, "/redirected").AbsoluteUri;
            return;
        }

        context.Response.Headers["x-amz-request-id"] = "fixture";
        switch (context.Request.Method)
        {
            case "PUT":
                Interlocked.Increment(ref _puts);
                using (var body = new MemoryStream(1024))
                {
                    await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                    _probe = body.ToArray();
                }

                context.Response.Headers.ETag = "\"probe\"";
                break;
            case "GET" when context.Request.Query.ContainsKey("list-type"):
                Interlocked.Increment(ref _lists);
                context.Response.ContentType = "application/xml";
                await context.Response.WriteAsync("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><IsTruncated>false</IsTruncated><KeyCount>0</KeyCount></ListBucketResult>", context.RequestAborted);
                break;
            case "GET":
                Interlocked.Increment(ref _gets);
                context.Response.ContentLength = _probe?.Length ?? 0;
                context.Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture);
                await context.Response.Body.WriteAsync(_probe ?? [], context.RequestAborted);
                break;
            case "DELETE":
                Interlocked.Increment(ref _deletes);
                _probe = null;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        ReleaseResolution();
        ReleaseConnect();
        try
        {
            await _app.DisposeAsync();
        }
        finally
        {
            _certificate.Dispose();
        }
    }

    private sealed class CountingProxy : IWebProxy
    {
        private int _routingLookups;
        public int RoutingLookups => Volatile.Read(ref _routingLookups);
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            Interlocked.Increment(ref _routingLookups);
            return new UriBuilder(destination) { Scheme = Uri.UriSchemeHttp, Host = "customer-proxy.test" }.Uri;
        }

        // .NET telemetry also calls IsBypassed with UseProxy=false; GetProxy identifies transport routing.
        public bool IsBypassed(Uri host) => false;
    }
}
