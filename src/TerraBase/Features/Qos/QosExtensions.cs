using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

internal static class QosExtensions
{
    extension(HttpContext context)
    {
        internal void ReleaseRequestLease() => context.Features.Get<QosRequestLease>()?.Release();
    }

    public static IServiceCollection AddTerraBaseQos(this IServiceCollection services) => services
        .AddSingleton<IQosAdmission, QosAdmission>()
        .AddSingleton<IStorageQuota, StorageQuota>();

    public static WebApplication UseTerraBaseQosMemory(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>().Count is not > 0)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var budget = context.RequestServices.GetRequiredService<IMemoryBudget>();
            using var scope = QosContext.Enter(null, budget);
            try
            {
                using var lookup = budget.TryReserve(16 * 1024, null) ?? throw new QosMemoryException();
                context.Features.Set(new LookupMemory(lookup));
                await next(context).ConfigureAwait(false);
            }
            catch (QosMemoryException) when (!context.Response.HasStarted)
            {
                await RejectTenantMemoryAsync(context).ConfigureAwait(false);
            }
        });
        return app;
    }

    public static WebApplication UseTerraBaseQos(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use((context, next) => context.Features.Get<ApiKey>() is null && context.User.Identity?.IsAuthenticated != true
            ? next(context)
            : AdmitAsync(context, next, context.RequestServices.GetRequiredService<IMemoryBudget>()));
        return app;
    }

    private static async Task AdmitAsync(HttpContext context, RequestDelegate next, IMemoryBudget budget)
    {
        var resolved = await ResolveTenantAsync(context).ConfigureAwait(false);

        context.Features.Get<LookupMemory>()?.Reservation.Dispose();
        if (resolved is Unavailable error)
        {
            await ErrorResponse.From(error).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var tenant = resolved switch
        {
            TenantContext known => known,
            Done or Unavailable => null,
        };

        var retryAfter = 1;
        var admission = context.RequestServices.GetRequiredService<IQosAdmission>();
        using var lease = tenant is not null ? admission.TryEnter(tenant, out retryAfter) : null;
        if (tenant is not null && lease is null)
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers.CacheControl = "no-store";
            await BadRequests.WriteAsync(context, StatusCodes.Status429TooManyRequests, "tenant_busy", "organization request budget exceeded", null).ConfigureAwait(false);
            return;
        }

        if (lease is not null)
        {
            context.Features.Set(new QosRequestLease(lease));
        }

        if (context.Request.ContentLength > DatabaseRoute.MaxRequestBytes)
        {
            await BadRequests.WriteAsync(context, StatusCodes.Status413PayloadTooLarge, "payload_too_large", "the request body exceeds the size limit", null).ConfigureAwait(false);
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        {
            bodyLimit.MaxRequestBodySize = Math.Min(bodyLimit.MaxRequestBodySize ?? long.MaxValue, DatabaseRoute.MaxRequestBytes);
        }

        using var scope = QosContext.Enter(tenant, admission: admission);
        var bodyBytes = context.Request.ContentLength ?? 0;
        using var body = budget.TryReserve(16 * 1024 + bodyBytes * 3);
        if (body is null)
        {
            await RejectTenantMemoryAsync(context).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Request.Body;
        using var streaming = context.Request.ContentLength is null
            && context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
                ? new QosBodyStream(originalBody, budget) : null;
        if (streaming is not null)
        {
            context.Request.Body = streaming;
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Request.Body = originalBody;
        }
    }

    private static async Task RejectTenantMemoryAsync(HttpContext context)
    {
        context.Response.Clear();
        context.Response.Headers.RetryAfter = "1";
        context.Response.Headers.CacheControl = "no-store";
        await BadRequests.WriteAsync(context, StatusCodes.Status429TooManyRequests, "tenant_memory", "organization memory budget exceeded", null).ConfigureAwait(false);
    }

    private readonly union QosTenant(TenantContext, Done, Unavailable);

    private static async ValueTask<QosTenant> ResolveTenantAsync(HttpContext context)
    {
        if (context.Features.Get<ProjectAccess>() is { } access)
        {
            return await ResolvePlanAsync(context, access.OrganizationId).ConfigureAwait(false);
        }
        if (context.Features.Get<ApiKey>() is { } key)
        {
            return await ResolvePlanAsync(context, key.OrganizationId).ConfigureAwait(false);
        }
        if (context.User.Identity?.IsAuthenticated == true
            && context.Request.RouteValues["organization"] as string is string organizationId)
        {
            return await context.RequestServices.GetRequiredService<IManagementService>()
                .GetOrganizationAsync(context.User.UserId(), organizationId, context.RequestAborted).ConfigureAwait(false) switch
            {
                Organization organization => new TenantContext(organization.Id, organization.Plan),
                NotFound => new Done(),
                Unavailable error => error,
            };
        }
        if (context.GetEndpoint()?.Metadata.GetMetadata<ManagementUserRoute>() is not null
            && UserAuthExtensions.Identity(context.User)?.FindFirst("project_id") is null
            && context.User.Identity?.IsAuthenticated == true
            && context.Request.RouteValues["project"] as string is string projectId)
        {
            return await context.RequestServices.GetRequiredService<IProjectCatalog>()
                .ResolveProjectAsync(projectId, context.User.UserId(), createIn: null, context.RequestAborted).ConfigureAwait(false) switch
            {
                string organization => await ResolvePlanAsync(context, organization).ConfigureAwait(false),
                NotFound => new Done(),
                Unavailable error => error,
            };
        }
        if (context.Features.Get<ProjectToken>()?.ProjectId is { } project
            && context.Request.RouteValues["project"] as string == project)
        {
            return await context.RequestServices.GetRequiredService<IProjectCatalog>()
                .ResolveProjectAsync(project, context.User.UserId(), createIn: null, context.RequestAborted).ConfigureAwait(false) switch
            {
                string organization => await ResolvePlanAsync(context, organization).ConfigureAwait(false),
                NotFound when context.User.FindFirst("org_id")?.Value is { } tokenOrganization =>
                    await ResolvePlanAsync(context, tokenOrganization).ConfigureAwait(false),
                NotFound => new Done(),
                Unavailable error => error,
            };
        }

        return new Done();
    }

    private static async ValueTask<QosTenant> ResolvePlanAsync(HttpContext context, string organizationId) =>
        await context.RequestServices.GetRequiredService<ITenantDirectory>()
            .PlanAsync(organizationId, context.RequestAborted).ConfigureAwait(false) switch
        {
            PlanTier plan => new TenantContext(organizationId, plan),
            Unavailable error => error,
        };

    private sealed record LookupMemory(IMemoryReservation Reservation);
}
