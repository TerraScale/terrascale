using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Documents;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

/// <summary>Registers organization resource controls and request admission.</summary>
public static class QosExtensions
{
    /// <summary>Registers bounded node-local request admission.</summary>
    public static IServiceCollection AddTerraBaseQos(this IServiceCollection services) => services
        .AddSingleton<IQosAdmission, QosAdmission>()
        .AddSingleton<IStorageQuota, StorageQuota>();

    /// <summary>Admits process memory before authentication can materialize catalog credentials.</summary>
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
                using var lookup = budget.TryReserve(MemoryLane.Foreground, 16 * 1024, null) ?? throw new QosMemoryException();
                context.Features.Set(new LookupMemory(lookup));
                await next(context).ConfigureAwait(false);
            }
            catch (QosMemoryException) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.Headers.RetryAfter = "1";
                context.Response.Headers.CacheControl = "no-store";
                await BadRequests.WriteAsync(context, StatusCodes.Status429TooManyRequests, "tenant_memory", "organization memory budget exceeded", null).ConfigureAwait(false);
            }
        });
        return app;
    }

    /// <summary>Admits authorized requests before forwarding or reading their bodies.</summary>
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
        TenantContext? tenant = null;
        if (context.Features.Get<ProjectAccess>() is { } access)
        {
            var plan = await context.RequestServices.GetRequiredService<ITenantDirectory>()
                .PlanAsync(access.OrganizationId, context.RequestAborted).ConfigureAwait(false);
            tenant = new TenantContext(access.OrganizationId, plan);
        }
        else if (context.Features.Get<ApiKey>() is { } key)
        {
            var plan = await context.RequestServices.GetRequiredService<ITenantDirectory>()
                .PlanAsync(key.OrganizationId, context.RequestAborted).ConfigureAwait(false);
            tenant = new TenantContext(key.OrganizationId, plan);
        }
        else if (context.User.Identity?.IsAuthenticated == true
            && (context.Request.RouteValues["organization"] as string
                ?? context.Features.Get<LegacyManagementRoute>()?.OrganizationId) is string organizationId)
        {
            var organization = await context.RequestServices.GetRequiredService<IManagementService>()
                .GetOrganizationAsync(context.User.UserId(), organizationId, context.RequestAborted).ConfigureAwait(false);
            if (organization is not null)
            {
                tenant = new TenantContext(organization.Id, organization.Plan);
            }
        }
        else if (context.Features.Get<ProjectToken>()?.ProjectId is { } project
            && context.Request.RouteValues["project"] as string == project)
        {
            // First-database creation resolves its project after binding; existing projects still share admission.
            var organization = await context.RequestServices.GetRequiredService<IProjectCatalog>()
                .ResolveProjectAsync(project, context.User.UserId(), create: false, context.RequestAborted).ConfigureAwait(false);
            if (organization is not null)
            {
                var plan = await context.RequestServices.GetRequiredService<ITenantDirectory>()
                    .PlanAsync(organization, context.RequestAborted).ConfigureAwait(false);
                tenant = new TenantContext(organization, plan);
            }
        }

        context.Features.Get<LookupMemory>()?.Reservation.Dispose();

        var retryAfter = 1;
        using var lease = tenant is not null
            ? context.RequestServices.GetRequiredService<IQosAdmission>().TryEnter(tenant, out retryAfter) : null;
        if (tenant is not null && lease is null)
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers.CacheControl = "no-store";
            await BadRequests.WriteAsync(context, StatusCodes.Status429TooManyRequests, "tenant_busy", "organization request budget exceeded", null).ConfigureAwait(false);
            return;
        }

        if (context.Request.ContentLength > DocumentRoute.MaxRequestBytes)
        {
            await BadRequests.WriteAsync(context, StatusCodes.Status413PayloadTooLarge, "payload_too_large", "the request body exceeds the size limit", null).ConfigureAwait(false);
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        {
            bodyLimit.MaxRequestBodySize = Math.Min(bodyLimit.MaxRequestBodySize ?? long.MaxValue, DocumentRoute.MaxRequestBytes);
        }

        if (tenant is not null)
        {
            context.RequestServices.GetRequiredService<IDocumentCacheQuota>().Reconcile(tenant);
        }

        using var scope = QosContext.Enter(tenant);
        // Known lengths reserve before decoding; chunked bodies reserve before each bounded read.
        var bodyBytes = context.Request.ContentLength ?? 0;
        using var body = budget.TryReserve(MemoryLane.Foreground, 16 * 1024 + bodyBytes * 3);
        if (body is null)
        {
            context.Response.Headers.RetryAfter = "1";
            context.Response.Headers.CacheControl = "no-store";
            await BadRequests.WriteAsync(context, StatusCodes.Status429TooManyRequests, "tenant_memory", "organization memory budget exceeded", null).ConfigureAwait(false);
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

    private sealed record LookupMemory(IMemoryReservation Reservation);
}
