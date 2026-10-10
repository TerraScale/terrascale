using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using TerraBase.Configuration;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.ApiReference;

/// <summary>
/// The public API reference: the OpenAPI document and Scalar, served anonymously on the client listener only. The document
/// lists only client-listener operations, with their security schemes and authentication failures.
/// </summary>
public static class ApiReferenceExtensions
{
    /// <summary>Security scheme of public operations: a ts-auth project access token as a bearer token.</summary>
    public const string TsAuthScheme = "TsAuth";

    /// <summary>Shapes the OpenAPI document <paramref name="documentName"/>; register it before other operation transformers.</summary>
    public static IServiceCollection AddTerraBaseApiReference(this IServiceCollection services, string documentName) =>
        services.AddOpenApi(documentName, options =>
        {
            // Admin-listener operations (endpoints without client metadata) stay out of the public document.
            options.ShouldInclude = description => description.ActionDescriptor.EndpointMetadata.OfType<ListenerMetadata>()
                .Any(metadata => metadata.Listeners.Contains(TerraBaseListener.Client))
                && description.RelativePath?.StartsWith("v1/management/", StringComparison.OrdinalIgnoreCase) != true
                && description.RelativePath?.StartsWith("management/", StringComparison.OrdinalIgnoreCase) != true
                && !(description.HttpMethod == "PUT" && description.RelativePath == "v1/projects/{project}/databases/{database}");
            options.AddDocumentTransformer((document, context, _) =>
            {
                var terrabase = context.ApplicationServices.GetRequiredService<TerraBaseOptions>();
                document.Info.Version = ServiceVersion.Value;
                // Relative, so the document works on every regional host and behind any proxy.
                document.Servers = [new OpenApiServer { Url = "/", Description = $"TerraBase node in region {terrabase.Region}" }];
                document.Components ??= new OpenApiComponents();
                document.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
                document.Extensions["x-tagGroups"] = new JsonNodeExtension(JsonNode.Parse("""
                    [{"name":"Management","tags":["Databases","Backups","Indexes","Schema","Usage"]},
                     {"name":"Data","tags":["Collections","Documents","Queries","Transactions","Live"]},
                     {"name":"System","tags":["Health"]}]
                    """)!);
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal)
                {
                    [TsAuthScheme] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        Description = terrabase.AuthIssuer is { } issuer
                            ? $"Authorization: Bearer <ts-auth project access token> issued by {issuer}. project_id must name the route's project; scope must grant the operation."
                            : "Authorization: Bearer <ts-auth project access token>. This node has no issuer configured; project access token requests receive 401.",
                    },
                };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer(async (operation, context, cancellationToken) =>
            {
                foreach (var parameter in context.Description.ActionDescriptor.EndpointMetadata.OfType<OpenApiParameter>())
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(parameter);
                }

                var policies = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>()
                    .Select(data => data.Policy).OfType<string>().ToList();
                if (policies.Count == 0)
                {
                    return;
                }

                var scopes = policies.Where(scope => ApiKeyScopes.All.Contains(scope)
                    || ApiKeyScopes.ProjectTokenScopes.Contains(scope)).ToList();
                if (policies.Contains(UserAuthExtensions.SubscriptionPolicy))
                {
                    scopes.Add(ApiKeyScopes.ShapesRead);
                }

                // EXPLAIN validates its exact project scope in the endpoint after JWT authentication.
                if (operation.OperationId == "explainSql" && policies.Contains(UserAuthExtensions.PolicyName))
                {
                    scopes.Add(ApiKeyScopes.DatabasesRead);
                }

                if (scopes.Count == 0)
                {
                    return;
                }

                // HTTP bearer requirements cannot carry OAuth scopes; document the JWT claim requirements separately.
                operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(TsAuthScheme, context.Document)] = [] }];
                operation.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
                operation.Extensions["x-required-scopes"] = new JsonNodeExtension(new JsonArray([.. scopes.Select(scope => JsonValue.Create(scope))]));
                operation.Description += $"\n\nRequired JWT scopes: {string.Join(", ", scopes)}.";
                operation.Responses ??= [];
                var error = await context.GetOrCreateSchemaAsync(typeof(ErrorBody), cancellationToken: cancellationToken);
                operation.Responses.TryAdd("401", Error("Unauthorized: missing, unknown or expired credential.", error));
                operation.Responses.TryAdd("403", Error("Forbidden: the access token lacks the required scope (forbidden_scope).", error));
                operation.Responses.TryAdd("404", Error("Not found: the project or narrowed database is not reachable (unknown_database).", error));
            });
        });

    /// <summary>Adds examples and error codes after request-body and region metadata have been generated.</summary>
    public static IServiceCollection AddTerraBaseApiExamples(this IServiceCollection services, string documentName) =>
        services.AddOpenApi(documentName, options => options.AddOperationTransformer((operation, _, _) =>
        {
            ApiExamples.Apply(operation);
            return Task.CompletedTask;
        }));

    /// <summary>
    /// Maps <c>/openapi/{documentName}.json</c>, Scalar at <c>/scalar/</c> and the <c>GET /</c> redirect
    /// (<see cref="RootRedirectEndpoint"/>) on the client listener; none needs credentials.
    /// </summary>
    public static WebApplication MapTerraBaseApiReference(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<EndpointOptions>();
        app.MapOpenApi(options.OpenApiRoute).WithMetadata(ListenerMetadata.Client);
        app.MapScalarApiReference(options.ScalarRoute, scalar => scalar
                .WithTitle(options.Title)
                // System fonts, no telemetry and no AI agent: the page talks to nothing but this node.
                .DisableDefaultFonts()
                .DisableTelemetry()
                .DisableAgent()
                .WithOpenApiRoutePattern(options.OpenApiRoute)
                .AddDocument(options.Version, options.Title, options.OpenApiRoute, isDefault: true))
            .WithMetadata(ListenerMetadata.Client);
        return app;
    }

    private static OpenApiResponse Error(string description, IOpenApiSchema schema) => new()
    {
        Description = description,
        Content = new Dictionary<string, IOpenApiMediaType>(StringComparer.Ordinal)
        {
            ["application/json"] = new OpenApiMediaType { Schema = schema },
        },
    };
}
