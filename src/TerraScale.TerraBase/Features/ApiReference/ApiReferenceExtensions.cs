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
    /// <summary>Security scheme of data-plane operations: a <c>tb_</c> API key as a bearer token.</summary>
    public const string ApiKeyScheme = "ApiKey";

    /// <summary>Security scheme of Management operations: a ts-auth (OIDC) access token as a bearer token.</summary>
    public const string TsAuthScheme = "TsAuth";

    /// <summary>Shapes the OpenAPI document <paramref name="documentName"/>; register it before other operation transformers.</summary>
    public static IServiceCollection AddTerraBaseApiReference(this IServiceCollection services, string documentName) =>
        services.AddOpenApi(documentName, options =>
        {
            // Admin-listener operations (endpoints without client metadata) stay out of the public document.
            options.ShouldInclude = description => description.ActionDescriptor.EndpointMetadata.OfType<ListenerMetadata>()
                .Any(metadata => metadata.Listeners.Contains(TerraBaseListener.Client));
            options.AddDocumentTransformer((document, context, _) =>
            {
                var terrabase = context.ApplicationServices.GetRequiredService<TerraBaseOptions>();
                document.Info.Version = ServiceVersion.Value;
                // Relative, so the document works on every regional host and behind any proxy.
                document.Servers = [new OpenApiServer { Url = "/", Description = $"TerraBase node in region {terrabase.Region}" }];
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal)
                {
                    [ApiKeyScheme] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "tb_ API key",
                        Description = "Data plane: Authorization: Bearer <tb_ API key>. The requirement lists the scope the operation needs.",
                    },
                    [TsAuthScheme] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        Description = terrabase.AuthIssuer is { } issuer
                            ? $"Management API: Authorization: Bearer <ts-auth OIDC access token> issued by {issuer}."
                            : "Management API: Authorization: Bearer <ts-auth OIDC access token>. This node has no issuer configured and answers 401.",
                    },
                };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer(async (operation, context, cancellationToken) =>
            {
                var policies = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>()
                    .Select(data => data.Policy).OfType<string>().ToList();
                if (policies.Count == 0)
                {
                    return;
                }

                var scopes = policies.Where(ApiKeyScopes.All.Contains).ToList();
                var scheme = scopes.Count > 0 ? ApiKeyScheme
                    : policies.Contains(UserAuthExtensions.PolicyName) || policies.Contains(UserAuthExtensions.SubscriptionPolicy) ? TsAuthScheme : null;
                if (scheme is null)
                {
                    return;
                }

                operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, context.Document)] = scopes }];
                operation.Responses ??= [];
                var error = await context.GetOrCreateSchemaAsync(typeof(ErrorBody), cancellationToken: cancellationToken);
                operation.Responses.TryAdd("401", Error("Unauthorized: missing, unknown or expired credential.", error));
                if (scheme == ApiKeyScheme)
                {
                    operation.Responses.TryAdd("403", Error("Forbidden: the API key lacks the required scope (forbidden_scope).", error));
                }
            });
        });

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
