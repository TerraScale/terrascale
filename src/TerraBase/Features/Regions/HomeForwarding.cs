using System.Net;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Regions;

internal static class HomeForwarding
{
    internal const string ForwardedForHeader = "X-TerraBase-Forwarded-For";

    private static readonly HashSet<string> Unforwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Proxy-Connection", "Proxy-Authorization",
        "Content-Length", "Content-Type", "X-TerraScale-Region-Pin", "ts-replay-src", "ts-replay-failed", "X-Forwarded-For", "X-Forwarded-Host",
        "X-Forwarded-Proto", "Forwarded", ForwardedForHeader, InternalCredential.HeaderName, Management.Usage.PeerUsage.ScopeHeader,
    };

    private static readonly HashSet<string> Unreturned = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Trailer", "Upgrade", "Proxy-Connection",
    };

    public static WebApplication UseTerraBaseHomeForwarding(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            if (await HomeOfAsync(context).ConfigureAwait(false) is { } home)
            {
                await ForwardAsync(context, home).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        return app;
    }

    internal static string Host(string region) => region + ".api.terrascale.tech";

    private static async ValueTask<HomeNode?> HomeOfAsync(HttpContext context)
    {
        if (Listeners.ArrivedOn(context) != TerraBaseListener.Client || Listeners.IsForwarded(context) || context.GetEndpoint() is null
            || context.Request.RouteValues["project"] is not string project || context.Request.RouteValues.ContainsKey("organization")
            || context.User.Identity?.IsAuthenticated != true || context.User.FindFirst("project_id")?.Value != project)
        {
            return null;
        }

        var database = context.Request.RouteValues["database"] as string;
        if (database is null && HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.Value?.TrimEnd('/') == $"/v1/projects/{project}/databases")
        {
            return null;
        }

        var services = context.RequestServices;
        var organization = await services.GetRequiredService<IProjectCatalog>().ProjectOrganizationAsync(project, context.RequestAborted).ConfigureAwait(false);
        if (organization is string found)
        {
            if (database is null
                || await services.GetRequiredService<IManagementService>().FindDatabaseRecordAsync(found, project, database, context.RequestAborted).ConfigureAwait(false) is not NotFound)
            {
                return null;
            }
        }
        else if (organization is not NotFound)
        {
            return null;
        }

        return await services.GetRequiredService<IHomeLocator>().LocateAsync(project, database, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the client request's headers onto <paramref name="request"/> for a peer in <paramref name="region"/> and signs
    /// it with the peer credential, so the peer serves it as the same client request.
    /// </summary>
    internal static void Sign(HttpContext context, HttpRequestMessage request, string region)
    {
        foreach (var (name, values) in context.Request.Headers)
        {
            if (!Unforwarded.Contains(name) && !name.StartsWith(':'))
            {
                request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }

        request.Headers.Host = Host(region);
        request.Headers.TryAddWithoutValidation(ForwardedForHeader, (IpAllowlists.ClientOf(context) ?? IPAddress.None).ToString());
        context.RequestServices.GetRequiredService<InternalCredential>().Sign(request.Headers);
    }

    internal static async Task ForwardAsync(HttpContext context, HomeNode home, byte[]? body = null)
    {
        var services = context.RequestServices;
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
            new Uri(home.Address, context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent()))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        if (body is not null || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        {
            request.Content = body is null ? new StreamContent(context.Request.Body) : new ByteArrayContent(body);
            if (context.Request.ContentType is { } contentType)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        Sign(context, request, home.Region);
        HttpResponseMessage response;
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            response = await services.GetRequiredService<IHttpClientFactory>().CreateClient(HomeLocator.HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-TerraBase-Database-Home"] = home.Region;
            await BadRequests.WriteAsync(context, StatusCodes.Status421MisdirectedRequest, "wrong_region",
                $"the database is served in {home.Region}: send it to https://{Host(home.Region)}", op: null).ConfigureAwait(false);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (!Unreturned.Contains(name))
                {
                    context.Response.Headers[name] = values.ToArray();
                }
            }

            var elapsed = TimeProvider.System.GetElapsedTime(started).TotalMilliseconds;
            context.Response.Headers.Append("Server-Timing",
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"home;dur={elapsed:0.0};desc=\"{home.Region}\""));

            await using var answer = await response.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
            await answer.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
