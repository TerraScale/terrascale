using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi;
using TerraBase.Configuration;
using TerraBase.Features.Management;
using TerraBase.Features.Postgres;
using TerraBase.Features.Regions;
using TerraBase.Features.Pressure;

namespace TerraBase.Hosting;

public enum TerraBaseListener
{
    Internal,
    Client,
    Metrics,
    Postgres,
}
public sealed record ListenerMetadata(params IReadOnlyList<TerraBaseListener> Listeners)
{
    public static ListenerMetadata Internal { get; } = new(TerraBaseListener.Internal);
    public static ListenerMetadata Client { get; } = new(TerraBaseListener.Client, TerraBaseListener.Internal);
    public static ListenerMetadata Probe { get; } = new(TerraBaseListener.Internal, TerraBaseListener.Client);
    public static ListenerMetadata Management { get; } = new(TerraBaseListener.Internal, TerraBaseListener.Client);
    public static ListenerMetadata Metrics { get; } = new(TerraBaseListener.Internal, TerraBaseListener.Metrics);
}
public sealed record AnsweringRegion(string Region);
public static class Listeners
{
    private static readonly object ListenerKey = typeof(TerraBaseListener);
    public static TerraBaseListener? ArrivedOn(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ListenerKey, out var tag) == true
            ? tag as TerraBaseListener?
            : null;
    }
    internal static IConnectionItemsFeature AcceptedBy(TerraBaseListener listener) => new AcceptedConnection(listener);
    public static string RegionOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<AnsweringRegion>()?.Region ?? context.RequestServices.GetRequiredService<TerraBaseOptions>().Region;
    }
    private const string RegionalHostSuffix = ".api.terrascale.tech";
    private static bool IsRegionalHost(string host, out string? region)
    {
        region = null;
        if (!host.EndsWith(RegionalHostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var label = host[..^RegionalHostSuffix.Length].ToLowerInvariant();
        region = TerraBaseOptions.IsRegion(label) ? label : null;
        return true;
    }

    private static string RegionalHosts(IEnumerable<string> regions) => string.Join(", ", regions.Select(name => name + RegionalHostSuffix));
    private static async Task<SortedSet<string>> ServedRegionsAsync(HttpContext context)
    {
        return [.. await context.RequestServices.GetRequiredService<IRegionCatalog>().ServingAsync(context.RequestAborted).ConfigureAwait(false)];
    }
    private static async Task RefusePinAsync(HttpContext context, StringValues pin, string? host)
    {
        var region = RegionOf(context);
        var served = await ServedRegionsAsync(context);
        var regions = string.Join(", ", served);
        var (code, message) = (pin.Count == 1 && TerraBaseOptions.IsRegion(pin[0]), host) switch
        {
            (false, _) => ("invalid_region_pin", $"X-TerraScale-Region-Pin takes exactly one region ID; the serving regions are {regions}"),
            (true, not null) => ("region_pin_mismatch",
                $"X-TerraScale-Region-Pin names another region than the host {host}{RegionalHostSuffix}; send the pin or the regional host, not both"),
            (true, null) when !served.Contains(pin[0]!) => ("region_pin_mismatch",
                $"the pinned region has no machine to answer, so {region} received the request; pin one of the serving regions ({regions}) or drop X-TerraScale-Region-Pin"),
            (true, null) => ("region_pin_mismatch",
                $"the request is pinned to another region and reached {region}; retry, send it to the pinned region's host (<region>{RegionalHostSuffix}), or drop X-TerraScale-Region-Pin to be answered by the nearest region. A database answers only in its regions: pin one of them or add the pinned region to its placement"),
        };
        await BadRequests.WriteAsync(context, StatusCodes.Status421MisdirectedRequest, code, message, op: null);
    }
    public static void ReplayIn(HttpContext context, string regions, bool fallbackSelf = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ArrivedOn(context) == TerraBaseListener.Client && !context.Request.Headers.ContainsKey("ts-replay-src"))
        {
            context.Response.Headers["ts-replay"] = fallbackSelf ? $"region={regions};fallback=self" : $"region={regions}";
        }
    }
    public static IServiceCollection AddTerraBaseRegionOpenApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOpenApi("v1", options => options.AddOperationTransformer(async (operation, context, cancellationToken) =>
        {
            if (context.Description.ActionDescriptor.EndpointMetadata.OfType<ListenerMetadata>()
                .Any(metadata => metadata.Listeners.Contains(TerraBaseListener.Client)))
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "X-TerraScale-Region-Pin",
                    In = ParameterLocation.Header,
                    Description = "Optional single raw region ID. An empty, duplicate or malformed pin returns 421 invalid_region_pin and a pin naming another region 421 region_pin_mismatch, both before execution and with recovery guidance; behind the edge a mismatched pin also answers ts-replay: region=<pin>.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                });
                operation.Responses ??= [];
                operation.Responses["421"] = new OpenApiResponse
                {
                    Description = "Misdirected Request; X-TerraBase-Region identifies the serving node.",
                    Content = new Dictionary<string, IOpenApiMediaType>(StringComparer.Ordinal)
                    {
                        ["application/json"] = new OpenApiMediaType { Schema = await context.GetOrCreateSchemaAsync(typeof(ErrorBody), cancellationToken: cancellationToken) },
                    },
                };
                foreach (var response in operation.Responses.Values.OfType<OpenApiResponse>())
                {
                    response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
                    response.Headers["X-TerraBase-Region"] = new OpenApiHeader
                    {
                        Description = "Region of the serving TerraBase node (client listener only).",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    };
                    response.Headers["X-TerraBase-Machine"] = new OpenApiHeader
                    {
                        Description = "Node of the serving TerraBase node (TS_NODE; local outside a cluster; client listener only).",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    };
                }
            }
        }));
        return services;
    }
    public static ListenOptions UseTerraBaseListener(this ListenOptions listen, TerraBaseListener listener)
    {
        ArgumentNullException.ThrowIfNull(listen);
        listen.Use(next => async connection =>
        {
            connection.Items[ListenerKey] = listener;
            using var drain = listen.ApplicationServices.GetRequiredService<Shutdown>().Drain(connection);
            var pressure = listener is TerraBaseListener.Client or TerraBaseListener.Postgres ? listen.ApplicationServices.GetRequiredService<IPressureSignals>() : null;
            pressure?.ConnectionOpened();
            try
            {
                await next(connection).ConfigureAwait(false);
            }
            finally
            {
                pressure?.ConnectionClosed();
            }
        });
        return listen;
    }
    public static bool LoopbackOnly(TerraBaseOptions options, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.PeersFile is null && environmentName == Environments.Development;
    }
    public static IPAddress InternalAddress(TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.PeersFile is null ? IPAddress.Loopback : PeersFile.SelfAddress(options);
    }
    public static void AddTerraBaseListeners(this KestrelServerOptions kestrel, TerraBaseOptions options, IPAddress internalAddress, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        ArgumentNullException.ThrowIfNull(options);
        var local = LoopbackOnly(options, environmentName);
        kestrel.Listen(internalAddress, options.InternalPort, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseTerraBaseListener(TerraBaseListener.Internal);
        });
        Bind(options.Port, TerraBaseListener.Client);
        Bind(options.MetricsPort, TerraBaseListener.Metrics);
        Bind(options.PostgresPort, TerraBaseListener.Postgres);

        void Bind(int port, TerraBaseListener listener)
        {
            void Configure(ListenOptions listen)
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseTerraBaseListener(listener);
                if (listener == TerraBaseListener.Postgres)
                {
                    listen.UseConnectionHandler<PostgresConnectionHandler>();
                }
            }
            if (local)
            {
                kestrel.Listen(IPAddress.Loopback, port, Configure);
            }
            else
            {
                kestrel.ListenAnyIP(port, Configure);
            }
        }
    }
    public static WebApplication UseTerraBaseListeners(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var internalRequests = app.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName)
            .CreateCounter<long>("terrabase.internal.authentication.requests", description: "Internal requests by credential verification outcome.");
        app.Use(async (context, next) =>
        {
            var arrivedOn = ArrivedOn(context);
            if (arrivedOn == TerraBaseListener.Internal)
            {
                var matched = context.RequestServices.GetRequiredService<InternalCredential>().Matches(context);
                internalRequests.Add(1, new KeyValuePair<string, object?>("credential", matched ? "matched" : "unmatched"));
                if (!matched)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }

            if (arrivedOn == TerraBaseListener.Client)
            {
                var region = RegionOf(context);
                var machine = context.RequestServices.GetRequiredService<TerraBaseOptions>().MachineId;
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers["X-TerraBase-Region"] = region;
                    context.Response.Headers["X-TerraBase-Machine"] = machine;
                    return Task.CompletedTask;
                });
                var hasPin = context.Request.Headers.TryGetValue("X-TerraScale-Region-Pin", out var pin);
                if (IsRegionalHost(context.Request.Host.Host, out var host))
                {
                    if (host is null)
                    {
                        await BadRequests.WriteAsync(context, StatusCodes.Status421MisdirectedRequest, "misdirected_request",
                            $"that host names no region; the regional hosts are {RegionalHosts(await ServedRegionsAsync(context))}", op: null);
                        return;
                    }
                    if (hasPin && (pin.Count != 1 || !string.Equals(pin[0], host, StringComparison.Ordinal)))
                    {
                        await RefusePinAsync(context, pin, host);
                        return;
                    }

                    if (!string.Equals(host, region, StringComparison.Ordinal))
                    {
                        var served = await ServedRegionsAsync(context);
                        if (context.Request.Headers.ContainsKey("ts-replay-src") || context.Request.Headers.ContainsKey("ts-replay-failed") || !served.Contains(host))
                        {
                            await BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "region_unavailable",
                                $"region {host} has no machine to answer; the regional hosts are {RegionalHosts(served)}", op: null);
                            return;
                        }
                        ReplayIn(context, host, fallbackSelf: true);
                        context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                        return;
                    }
                }
                else if (hasPin && (pin.Count != 1 || !string.Equals(pin[0], region, StringComparison.Ordinal)))
                {
                    if (pin.Count == 1 && TerraBaseOptions.IsRegion(pin[0]))
                    {
                        ReplayIn(context, pin[0]!);
                    }

                    await RefusePinAsync(context, pin, host: null);
                    return;
                }
            }

            var servedBy = context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>()?.Listeners ?? ListenerMetadata.Internal.Listeners;
            if (arrivedOn == TerraBaseListener.Internal
                && context.GetEndpoint()?.Metadata.GetMetadata<ProjectManagementRoute>() is not null
                && context.Features.Get<LegacyManagementRoute>() is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (arrivedOn is not { } listener || !servedBy.Contains(listener))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });
        return app;
    }

    private sealed class AcceptedConnection(TerraBaseListener listener) : IConnectionItemsFeature
    {
        public IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?> { [ListenerKey] = listener };
    }
}
