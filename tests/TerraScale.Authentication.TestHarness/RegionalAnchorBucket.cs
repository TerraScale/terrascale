using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TerraScale.Authentication.Tests;

/// <summary>A test-owned HTTPS object store for real binaries' encrypted restore anchors and small backups.</summary>
internal sealed class RegionalAnchorBucket : IAsyncDisposable
{
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly ConcurrentDictionary<string, byte[]> objects = new(StringComparer.Ordinal);
    private readonly WebApplication app;
    private readonly X509Certificate2 certificate;

    private RegionalAnchorBucket(WebApplication app, X509Certificate2 certificate)
    {
        this.app = app;
        this.certificate = certificate;
    }

    public Uri Address { get; private set; } = null!;

    public static async Task<RegionalAnchorBucket> StartAsync(string trustFile)
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        var after = before.AddDays(1);
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=ts-auth regional test CA", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(before, after);
        await File.WriteAllTextAsync(trustFile, root.ExportCertificatePem());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var issued = request.Create(root, before, after, RandomNumberGenerator.GetBytes(16));
        var certificate = issued.CopyWithPrivateKey(key);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 32 << 20;
            kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate));
        });
        var bucket = new RegionalAnchorBucket(builder.Build(), certificate);
        bucket.app.Run(bucket.RespondAsync);
        try
        {
            await bucket.app.StartAsync();
            bucket.Address = new Uri(bucket.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return bucket;
        }
        catch
        {
            await bucket.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.app.DisposeAsync();
        this.certificate.Dispose();
    }

    private async Task RespondAsync(HttpContext context)
    {
        var key = context.Request.Path.Value!;
        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Query.ContainsKey("list-type"))
        {
            var prefix = "/ts757/" + context.Request.Query["prefix"];
            var after = "/ts757/" + context.Request.Query["start-after"];
            var result = new XElement(S3 + "ListBucketResult", new XElement(S3 + "IsTruncated", false),
                this.objects.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal) && string.CompareOrdinal(item.Key, after) > 0)
                    .OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new XElement(S3 + "Contents",
                        new XElement(S3 + "Key", item.Key["/ts757/".Length..]), new XElement(S3 + "Size", item.Value.Length))));
            context.Response.ContentType = "application/xml";
            await context.Response.WriteAsync(result.ToString(SaveOptions.DisableFormatting));
            return;
        }

        if (HttpMethods.IsPut(context.Request.Method) && !context.Request.Query.ContainsKey("uploadId"))
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            var bytes = body.ToArray();
            this.objects[key] = bytes;
            context.Response.Headers.ETag = '"' + Convert.ToHexStringLower(SHA256.HashData(bytes)) + '"';
            return;
        }

        if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) && this.objects.TryGetValue(key, out var stored))
        {
            context.Response.ContentLength = stored.Length;
            if (HttpMethods.IsGet(context.Request.Method))
            {
                await context.Response.Body.WriteAsync(stored, context.RequestAborted);
            }

            return;
        }

        if (HttpMethods.IsDelete(context.Request.Method))
        {
            this.objects.TryRemove(key, out _);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync("<Error><Code>NoSuchKey</Code></Error>");
    }
}
