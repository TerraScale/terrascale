using System.Net.Http.Headers;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Platform.Mcp;

public static class DashboardMcp
{
    internal const string ContextKey = "TerraScale.Dashboard/mcp-context";

    public static void AddDashboardMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "terrascale-dashboard", Title = "TerraScale Dashboard", Version = "1.0.0" };
            options.ServerInstructions = "TerraScale dashboard tools manage organizations, projects, databases, data, credentials, billing and profile. Calls are stateless: pass organizationId and projectId to choose a scope or omit them to use your current dashboard selection. Call get_context first to discover accessible IDs.";
        }).WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, cancellationToken) =>
            {
                try { return await next(request, cancellationToken); }
                catch (AccountAccessException error)
                {
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error.Message }] };
                }
            }))
            .WithTools<DashboardMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.Databases.DatabaseMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.Data.DataMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.ApiKeys.ApiKeyMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.Backups.BackupMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.Home.HomeMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.Notifications.NotificationMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.Profile.ProfileMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.Usage.UsageMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.Settings.SettingsMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.Billing.BillingMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.Onboarding.OnboardingMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.AuthenticationManagement.AuthenticationMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.Organizations.OrganizationMcpTools>()
            .WithTools<TerraScale.Dashboard.Features.QuerySubscriptions.QuerySubscriptionMcpTools>();
    }

    public static void MapDashboardMcp(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/mcp") && !context.Request.Host.Host.Equals("mcp.terrascale.tech", StringComparison.OrdinalIgnoreCase))
            { await next(context); return; }
            if (context.Request.Headers.Origin is { Count: > 0 } origins &&
                (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) ||
                 origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0 ||
                 !string.Equals(origin.GetLeftPart(UriPartial.Authority), context.Request.Scheme + "://" + context.Request.Host, StringComparison.OrdinalIgnoreCase)))
            { context.Response.StatusCode = 403; return; }
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site") { context.Response.StatusCode = 403; return; }
            if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header) ||
                !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter) ||
                await context.RequestServices.GetRequiredService<AccessTokens>().ResolveAsync(header.Parameter, context.RequestAborted) is not { } principal)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer realm=\"TerraScale MCP\", error=\"invalid_token\"";
                context.Response.StatusCode = 401;
                return;
            }
            context.Items[ContextKey] = principal;
            await next(context);
        });
        app.MapMcp("/mcp");
        app.MapMcp("/{**mcpPath}").RequireHost("mcp.terrascale.tech");
    }

    internal static AccountContext Scope(HttpContext? http, string? organizationId, string? projectId) => McpScope.Scope(http, organizationId, projectId);
}
