using TerraScale.Dashboard.Platform.Observability;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Shared.Databases;
using TerraScale.Dashboard.Shared.Validation;

namespace TerraScale.Dashboard.Platform.Mcp;

public static class DashboardMcp
{
    internal const string ContextKey = "TerraScale.Dashboard/mcp-context";

    // Only immutable tool metadata is shared. Each invocation constructs its target
    // from that request's services; no host, principal or tool instance is retained.
    private static readonly Lazy<McpServerTool[]> CachedTools = new(CreateTools, LazyThreadSafetyMode.ExecutionAndPublication);

    private static McpServerTool[] CreateTools()
    {
        Type[] toolTypes =
        [
            typeof(DashboardMcpTools),
            typeof(TerraScale.Dashboard.Features.Databases.DatabaseMcpTools),
            typeof(TerraScale.Dashboard.Features.Data.DataMcpTools),
            typeof(TerraScale.Dashboard.Features.ApiKeys.ApiKeyMcpTools),
            typeof(TerraScale.Dashboard.Features.Backups.BackupMcpTools),
            typeof(TerraScale.Dashboard.Features.Home.HomeMcpTools),
            typeof(TerraScale.Dashboard.Features.Notifications.NotificationMcpTools),
            typeof(TerraScale.Dashboard.Features.Profile.ProfileMcpTools),
            typeof(TerraScale.Dashboard.Features.Usage.UsageMcpTools),
            typeof(TerraScale.Dashboard.Features.Settings.SettingsMcpTools),
            typeof(TerraScale.Dashboard.Features.Billing.BillingMcpTools),
            typeof(TerraScale.Dashboard.Features.Onboarding.OnboardingMcpTools),
            typeof(TerraScale.Dashboard.Features.AuthenticationManagement.AuthenticationMcpTools),
            typeof(TerraScale.Dashboard.Features.Organizations.OrganizationMcpTools),
            typeof(TerraScale.Dashboard.Features.QuerySubscriptions.QuerySubscriptionMcpTools),
        ];
        return toolTypes.SelectMany(toolType => toolType.GetMethods()
            .Where(method => method.IsDefined(typeof(McpServerToolAttribute)))
            .Select(method => McpServerTool.Create(method, request => ActivatorUtilities.CreateInstance(
                request.Services ?? throw new InvalidOperationException("MCP request services are unavailable."), toolType),
                options: new() { SchemaCreateOptions = new() { TransformSchemaNode = NullableRequirements } })))
            .ToArray();
    }

    private static JsonNode NullableRequirements(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        if (schema is not JsonObject { } node || node["required"] is not JsonArray required || node["properties"] is not JsonObject properties) return schema;
        // Constructor parameters can be required for deserialization while their nullable output
        // properties are deliberately omitted. Keep the schema aligned with the serializer's policy.
        foreach (var property in context.TypeInfo.Properties)
        {
            if (!property.IsGetNullable && Nullable.GetUnderlyingType(property.PropertyType) is null) continue;
            if (properties[property.Name] is not JsonObject field || field["type"] is not JsonArray types || !types.Any(type => type?.GetValue<string>() == "null")) continue;
            var ignore = property.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), true)
                .OfType<JsonIgnoreAttribute>().FirstOrDefault()?.Condition ?? context.TypeInfo.Options.DefaultIgnoreCondition;
            if (ignore is not (JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault)) continue;
            for (var index = required.Count - 1; index >= 0; index--)
                if (required[index]?.GetValue<string>() == property.Name) required.RemoveAt(index);
        }
        return schema;
    }

    public static void AddDashboardMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddHostedService<McpRegistrationReclaimer>();
        services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "terrascale-dashboard", Title = "TerraScale Dashboard", Version = "1.0.0" };
            options.ServerInstructions = "TerraScale dashboard tools manage organizations, projects, databases, data, credentials, billing and profile. Calls are stateless: pass organizationId and projectId to choose a scope or omit them to use your current dashboard selection. Call get_context first to discover accessible IDs.";
        }).WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, cancellationToken) =>
            {
                var http = request.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
                try
                {
                    var result = await next(request, cancellationToken);
                    if (result.IsError == true) ServiceSloTelemetry.MarkBad(http);
                    return result;
                }
                catch (Exception error) when (error is AccountAccessException or OnboardingValidationException)
                {
                    if (error is AccountAccessException { StatusCode: >= 500 }) ServiceSloTelemetry.MarkBad(http);
                    return Failure(error.Message);
                }
                // Upstream refusals (missing collection, duplicate ID, stale version) and outages keep the
                // dashboard's own wording instead of the SDK's generic "An error occurred invoking" result.
                catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException or DatabaseWrongRegionException or DocumentWriteOutcomeUnknownException ||
                                              error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    if (DatabaseResults.Status(error) >= 500 || DatabaseResults.Status(error) == 408) ServiceSloTelemetry.MarkBad(http);
                    Log(request.Services).LogWarning("MCP tool {Tool} failed upstream with {Status} ({ErrorType})", LoggedTool(request.Params?.Name), DatabaseResults.Status(error), error.GetType().Name);
                    return Failure(DatabaseResults.Message(error));
                }
                catch (Stripe.StripeException error)
                {
                    ServiceSloTelemetry.MarkBad(http);
                    Log(request.Services).LogWarning("MCP tool {Tool} failed at the billing provider ({ErrorType})", LoggedTool(request.Params?.Name), error.GetType().Name);
                    return Failure("The billing provider could not complete the request. Return to billing to reconcile before trying again.");
                }
                catch (McpProtocolException error) when (error.ErrorCode is McpErrorCode.InvalidParams or McpErrorCode.MethodNotFound or McpErrorCode.ParseError) { throw; }
                catch { ServiceSloTelemetry.MarkBad(http); throw; }
            }))
            .WithTools(CachedTools.Value);
    }

    private static CallToolResult Failure(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static ILogger Log(IServiceProvider? services) =>
        services?.GetService<ILoggerFactory>()?.CreateLogger("TerraScale.Dashboard.Mcp") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private static string LoggedTool(string? name) => CachedTools.Value.Any(tool => tool.ProtocolTool.Name == name) ? name! : "[unknown]";

    public static void MapDashboardMcp(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/mcp") && (!McpOAuth.IsMcpHost(context) || context.Request.Path.StartsWithSegments("/.well-known")))
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
                // RFC 9728: the challenge points clients without a token at the OAuth sign-in metadata.
                context.Response.Headers.WWWAuthenticate = $"Bearer realm=\"TerraScale MCP\", resource_metadata=\"{McpOAuth.ResourceMetadataUrl(context)}\"" +
                    (context.Request.Headers.Authorization.Count == 0 ? "" : ", error=\"invalid_token\"");
                context.Response.StatusCode = 401;
                return;
            }
            // A 1 MiB opaque cursor can occupy six times that size in escaped JSON.
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
                bodyLimit.MaxRequestBodySize = 6 * 1024 * 1024 + 16 * 1024;
            context.Items[ContextKey] = principal;
            await next(context);
        });
        app.MapMcp("/mcp");
        app.MapMethods("/mcp", [HttpMethods.Get, HttpMethods.Delete], UnsupportedStatelessMethod);
        app.MapMcp("/").RequireHost("mcp.terrascale.tech");
        app.MapMethods("/", [HttpMethods.Get, HttpMethods.Delete], UnsupportedStatelessMethod).RequireHost("mcp.terrascale.tech");
    }

    private static IResult UnsupportedStatelessMethod(HttpContext http)
    {
        http.Response.Headers.Allow = "POST";
        return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    internal static AccountContext Scope(HttpContext? http, string? organizationId, string? projectId)
    {
        var context = McpScope.Account(http);
        var organization = string.IsNullOrEmpty(organizationId) ? context.Organization : context.Organizations.SingleOrDefault(item => item.Id == organizationId);
        if (organization is null || organization.RoleFor(context.UserId).Length == 0) throw new AccountAccessException("Organization is unavailable.", 404);
        var project = string.IsNullOrEmpty(projectId)
            ? organization.Id == context.Organization?.Id ? context.Project : organization.Projects.Values.FirstOrDefault(item => item.IsDefault)
            : organization.Projects.GetValueOrDefault(projectId);
        if (!string.IsNullOrEmpty(projectId) && project is null || project is not null && project.OrganizationId != organization.Id)
            throw new AccountAccessException("Project is unavailable.", 404);
        return new(context.Session, context.Account, context.Organizations, organization, project);
    }
}
