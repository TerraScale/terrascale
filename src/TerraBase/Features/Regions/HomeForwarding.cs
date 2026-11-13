using System.Globalization;
using System.Buffers;
using System.Net;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Errors;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Shapes;
using TerraBase.Features.Sql;
using TerraBase.Features.Qos;
using TerraBase.Features.Memory;
using TerraBase.Features.Replicas;
using System.Net.WebSockets;
using ZLogger;
using TerraBase.Tenancy;
using TerraBase.Features.Management;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Regions;

internal static class HomeForwarding
{
    internal sealed class ProxiedRequest;

    internal sealed class ForwardedAccounting
    {
        internal string? CompletedRegion { get; set; }
    }

    internal const string AccountingHeader = "X-TerraBase-Request-Accounting";

    internal const string PlacementMissMessage = "this region no longer holds the database; refresh its placement";
    internal const string PlacementMissHeader = "X-TerraBase-Placement-Miss";
    internal const string ForwardedForHeader = "X-TerraBase-Forwarded-For";
    internal const string PushThroughHeader = "TerraBase-Push-Through";
    internal const string PublishIdHeader = "TerraBase-Publish-Id";

    private static readonly HashSet<string> Unforwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Proxy-Connection", "Proxy-Authorization",
        "X-TerraScale-Caller", "Content-Length", "Content-Type", "X-TerraScale-Region-Pin", "ts-replay-src", "ts-replay-failed", "X-Forwarded-For", "X-Forwarded-Host",
        "X-Forwarded-Proto", "Forwarded", ForwardedForHeader, PushThroughHeader, PublishIdHeader, InternalCredential.HeaderName, Management.Usage.PeerUsage.ScopeHeader, ProxyRequestBudget.RemainingHeader, PlacementMissHeader, AccountingHeader,
    };

    private static readonly HashSet<string> Unreturned = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Trailer", "Upgrade", "Proxy-Connection", ShapeOverlay.CommitHeader, PlacementMissHeader, AccountingHeader,
    };

    public static WebApplication UseTerraBaseHomeForwarding(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            using var deadline = ProxyRequestBudget.For(context).Link(context.RequestAborted);
            HomeNode? home;
            try
            {
                home = await HomeOfAsync(context, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                await UnavailableAsync(context, context.RequestServices.GetRequiredService<TerraBaseOptions>().Region).ConfigureAwait(false);
                return;
            }

            if (home is not null)
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    await context.RequestServices.GetRequiredService<HomeSocketRelay>().RunAsync(context, home, context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                await ForwardAsync(context, home).ConfigureAwait(false);
                return;
            }

            if (context.Features.Get<ForwardingAuthorization.Refused>() is null)
            {
                await next(context).ConfigureAwait(false);
            }
        });
        return app;
    }

    internal static bool IsForwardable(string name) => !Unforwarded.Contains(name) && !name.StartsWith(':');

    internal static string Host(string region) => region + ".api.terrascale.tech";

    internal static string RetryUrl(HttpContext context, string region) =>
        "https://" + Host(region) + context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent();

    internal static Task UnavailableAsync(HttpContext context, string region)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.RetryAfter = "1";
        return BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "unavailable",
            $"the database region {region} could not be reached", op: null, region: region);
    }

    internal static bool Unanswered(HttpContext context, Exception error) =>
        error is HttpRequestException || (error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested);

    internal static Task UnansweredAsync(HttpContext context, string region, Exception error, bool sent = true)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
            || !sent)
        {
            return UnavailableAsync(context, region);
        }

        return BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, SqlErrors.WriteOutcomeUnknown,
            $"the request was sent toward the database's writer in {region}, which did not answer; it may have run there", op: null, region: region);
    }

    internal static async ValueTask<HomeNode?> HomeOfAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (Listeners.ArrivedOn(context) != TerraBaseListener.Client || Listeners.IsForwarded(context) || context.GetEndpoint() is null
            || context.Request.RouteValues["project"] is not string project || context.Request.RouteValues.ContainsKey("organization"))
        {
            return null;
        }

        if (context.User.Identity?.IsAuthenticated != true
            || context.Features.Get<ProjectAccess>() is null && context.Features.Get<ApiKeys.ApiKey>() is null
                && context.Features.Get<ProjectToken>() is null)
        {
            return null;
        }

        if (!context.WebSockets.IsWebSocketRequest
            && context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        {
            context.Features.Set(new ForwardedAccounting());
        }
        var owners = context.RequestServices.GetRequiredService<RegionalOwners>();
        if (!owners.Writer)
        {
            if (!await ForwardingAuthorization.CheckAsync(context, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            var local = context.RequestServices.GetRequiredService<TerraBaseOptions>();
            var node = (await owners.NodesAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(node => node.Region == local.Region && node.Name != local.MachineId);
            return node is null ? null : new HomeNode(local.Region, node.InternalListener(local.InternalPort));
        }

        var database = context.Request.RouteValues["database"] as string;
        if (database is null && HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.Value?.TrimEnd('/') == $"/v1/projects/{project}/databases")
        {
            return null;
        }

        var services = context.RequestServices;
        var options = services.GetRequiredService<TerraBaseOptions>();
        var management = services.GetRequiredService<IManagementService>();
        var peers = services.GetRequiredService<IRegionPeers>();
        var organization = await services.GetRequiredService<IProjectCatalog>().ProjectOrganizationAsync(project, cancellationToken).ConfigureAwait(false);
        if (organization is string found)
        {
            if (database is not null && context.GetEndpoint()?.Metadata.GetMetadata<OriginNoStore>() is not null
                && context.Request.RouteValues.ContainsKey("table")
                && await management.FindDatabaseRecordAsync(found, project, database, cancellationToken).ConfigureAwait(false) is ManagedDatabase shapeDatabase
                && shapeDatabase.Home(options.Region) is { } shapeHome
                && shapeHome != options.Region)
            {
                var tenant = new TenantContext(found, PlanTier.Hobby, project, shapeDatabase.Id);
                if (context.Request.Query["live"] == "true" && services.GetRequiredService<IDatabaseReplicas>().Holds(tenant))
                {
                    return null;
                }

                return await AuthorizedHomeAtAsync(shapeHome).ConfigureAwait(false);
            }

            if (database is not null
                && await management.FindDatabaseRecordAsync(found, project, database, cancellationToken).ConfigureAwait(false) is ManagedDatabase routedDatabase
                && (!routedDatabase.Placement(options.Region).PlacedIn(options.Region)
                    || context.GetEndpoint()?.Metadata.GetMetadata<DatabaseRegions.WriterReads>() is not null
                        && routedDatabase.Home(options.Region) != options.Region))
            {
                var destination = services.GetRequiredService<IHomeLocator>().Cached(project, database)?.Region ?? routedDatabase.Home(options.Region);
                return await AuthorizedHomeAtAsync(destination).ConfigureAwait(false);
            }

            if (database is null
                || await management.FindDatabaseRecordAsync(found, project, database, cancellationToken).ConfigureAwait(false) is not NotFound)
            {
                return null;
            }
        }
        else if (organization is not NotFound)
        {
            return null;
        }

        if (!await ForwardingAuthorization.CheckAsync(context, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return await services.GetRequiredService<IHomeLocator>().LocateAsync(project, database, cancellationToken).ConfigureAwait(false);

        async ValueTask<HomeNode?> AuthorizedHomeAtAsync(string region)
        {
            return await ForwardingAuthorization.CheckAsync(context, cancellationToken).ConfigureAwait(false)
                ? await HomeAtAsync(peers, region, cancellationToken).ConfigureAwait(false)
                : null;
        }
    }

    private static async ValueTask<HomeNode?> HomeAtAsync(IRegionPeers peers, string home, CancellationToken cancellationToken)
    {
        var peer = (await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Region == home);
        return peer is null ? null : new HomeNode(home, peer.Address);
    }

    internal static async Task<bool> ForwardBrowserAsync(HttpContext context, WebSocket socket, byte[] frame, Action authenticated, CancellationToken cancellationToken)
    {
        var auth = RequestJson.Read<SqlSocketRequest>(frame, SqlErrors.InvalidRequest) is SqlSocketRequest parsed ? parsed : null;
        context.Features.Set(new HomeSocketRelay.BrowserConnection(socket, auth?.Id));
        HomeNode? destination;
        using (var deadline = ProxyRequestBudget.For(context).Link(cancellationToken))
        {
            destination = await HomeOfAsync(context, deadline.Token).ConfigureAwait(false);
        }

        if (destination is not null)
        {
            await QosExtensions.AdmitAsync(context, async admitted =>
            {
                authenticated();
                await admitted.RequestServices.GetRequiredService<HomeSocketRelay>()
                    .RunAsync(admitted, destination, cancellationToken, socket, frame).ConfigureAwait(false);
            }, context.RequestServices.GetRequiredService<IMemoryBudget>()).ConfigureAwait(false);
        }

        return destination is not null || context.Features.Get<ForwardingAuthorization.Refused>() is not null || socket.State != WebSocketState.Open;
    }

    internal static void Sign(HttpContext context, HttpRequestMessage request, string region)
    {
        foreach (var (name, values) in context.Request.Headers)
        {
            if (IsForwardable(name))
            {
                request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }

        request.Headers.Host = Host(region);
        request.Headers.TryAddWithoutValidation(ForwardedForHeader, (IpAllowlists.ClientOf(context) ?? IPAddress.None).ToString());
        request.Headers.TryAddWithoutValidation(ProxyRequestBudget.RemainingHeader, ProxyRequestBudget.For(context).Milliseconds);
        context.RequestServices.GetRequiredService<InternalCredential>().Sign(request.Headers);
    }

    internal static async Task ForwardAsync(HttpContext context, HomeNode home, byte[]? body = null, string? pushThrough = null,
        Action<HttpResponseMessage>? answered = null, string? publishId = null)
    {
        if (Listeners.IsForwarded(context))
        {
            await UnavailableAsync(context, home.Region).ConfigureAwait(false);
            return;
        }

        if (!await ForwardingAuthorization.CheckAsync(context, context.RequestAborted).ConfigureAwait(false))
        {
            return;
        }
        var accounting = context.Features.Get<ForwardedAccounting>() ?? new ForwardedAccounting();
        context.Features.Set(accounting);
        using var deadline = ProxyRequestBudget.For(context).Link(context.RequestAborted);
        if (body is null && context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        {
            try
            {
                body = await ReadBodyAsync(context, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                await UnavailableAsync(context, home.Region).ConfigureAwait(false);
                return;
            }
            if (body is null)
            {
                await ErrorResponse.From(new PayloadTooLarge("the request body exceeds the size limit")).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        var first = await AttemptAsync(home).ConfigureAwait(false);
        if (first is null)
        {
            return;
        }

        using var initial = first;
        var response = initial;
        HttpResponseMessage? refreshedResponse = null;
        if (IsPlacementMiss(initial)
            && context.Request.RouteValues["project"] is string project)
        {
            HomeNode? fresh;
            try
            {
                fresh = await context.RequestServices.GetRequiredService<IHomeLocator>()
                    .RefreshAsync(project, context.Request.RouteValues["database"] as string, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                await UnavailableAsync(context, home.Region).ConfigureAwait(false);
                return;
            }
            if (fresh is not null && fresh.Region != home.Region)
            {
                refreshedResponse = await AttemptAsync(fresh).ConfigureAwait(false);
                if (refreshedResponse is null)
                {
                    return;
                }
                (response, home) = (refreshedResponse, fresh);
            }
        }

        using (refreshedResponse)
        {
            if (IsPlacementMiss(response))
            {
                await UnavailableAsync(context, home.Region).ConfigureAwait(false);
                return;
            }
            answered?.Invoke(response);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (!Unreturned.Contains(name) && name != PlacementMissHeader)
                {
                    if (name.Equals("Server-Timing", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.Headers.Append(name, values.ToArray());
                    }
                    else
                    {
                        context.Response.Headers[name] = values.ToArray();
                    }
                }
            }

            try
            {
                await using var answer = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await answer.CopyToAsync(context.Response.Body, deadline.Token).ConfigureAwait(false);
                if (!IsPlacementMiss(response))
                {
                    if (response.Headers.TryGetValues(AccountingHeader, out var ownership) && ownership.Contains("source"))
                    {
                        accounting.CompletedRegion = home.Region;
                    }
                    else
                    {
                        context.Features.Set(new ProxiedRequest());
                    }
                }
            }
            catch (Exception error) when (Unanswered(context, error) || error is IOException)
            {
                if (context.Response.HasStarted)
                {
                    context.Abort();
                }
                else
                {
                    context.Response.Clear();
                    await UnansweredAsync(context, home.Region, error, sent: !IsPlacementMiss(response)).ConfigureAwait(false);
                }
            }
        }

        async Task<HttpResponseMessage?> AttemptAsync(HomeNode target)
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                new Uri(target.Address, context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent()))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            SentContent? content = null;
            if (body is not null || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            {
                content = new SentContent(body ?? throw new InvalidOperationException("a forwarded body must be buffered"));
                request.Content = content;
                if (context.Request.ContentType is { } contentType)
                {
                    content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                }
            }

            Sign(context, request, target.Region);
            request.Headers.TryAddWithoutValidation(AccountingHeader, "source");
            if (pushThrough is not null)
            {
                request.Headers.TryAddWithoutValidation(PushThroughHeader, pushThrough);
            }

            if (publishId is not null)
            {
                request.Headers.TryAddWithoutValidation(PublishIdHeader, publishId);
            }

            var started = context.RequestServices.GetRequiredService<TimeProvider>().GetTimestamp();
            try
            {
                var result = await context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(HomeLocator.HttpClientName)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                var elapsed = context.RequestServices.GetRequiredService<TimeProvider>().GetElapsedTime(started).TotalMilliseconds;
                context.Response.Headers.Append("Server-Timing",
                    string.Create(CultureInfo.InvariantCulture, $"home;dur={elapsed:0.0};desc=\"{target.Region}\""));
                return result;
            }
            catch (Exception error) when (Unanswered(context, error))
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TerraBase.Features.Regions.HomeForwarding")
                    .ZLogWarning(error, $"The proxy leg to {target.Region:@Region} did not answer");
                await UnansweredAsync(context, target.Region, error, body is not { Length: > 0 } || content?.Sent == true).ConfigureAwait(false);
                return null;
            }
        }
    }

    internal static bool IsPlacementMiss(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.Contains(PlacementMissHeader)
        || response.StatusCode == HttpStatusCode.MisdirectedRequest && response.Headers.Contains("X-TerraBase-Database-Home");

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            for (int read; (read = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0;)
            {
                if (body.Length + read > DatabaseRoute.MaxRequestBytes)
                {
                    return null;
                }

                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return body.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class SentContent(byte[] body) : HttpContent
    {
        internal bool Sent { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Sent = true;
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.LongLength;
            return true;
        }
    }
}
