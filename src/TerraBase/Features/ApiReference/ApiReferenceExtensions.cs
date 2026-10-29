using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using TerraBase.Configuration;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Platform;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.ApiReference;

/// <summary>
/// The content of the public API reference document: client-listener operations, their security schemes, authentication
/// failures and examples. TerraScale.Endpoints maps the document and Scalar.
/// </summary>
public static class ApiReferenceExtensions
{
    /// <summary>Security scheme of public operations: a ts-auth project access token as a bearer token.</summary>
    public const string TsAuthScheme = "TsAuth";

    /// <summary>Shapes the OpenAPI document <paramref name="documentName"/>; register it before other operation transformers.</summary>
    public static IServiceCollection AddTerraBaseApiReference(this IServiceCollection services, string documentName) =>
        services.AddOpenApi(documentName, options =>
        {
            options.ShouldInclude = description => description.ActionDescriptor.EndpointMetadata.OfType<ListenerMetadata>()
                .Any(metadata => metadata.Listeners.Contains(TerraBaseListener.Client))
                && description.RelativePath?.StartsWith("v1/management/", StringComparison.OrdinalIgnoreCase) != true
                && description.RelativePath?.StartsWith("management/", StringComparison.OrdinalIgnoreCase) != true
                && !(description.HttpMethod == "PUT" && description.RelativePath == "v1/projects/{project}/databases/{database}");
            options.AddDocumentTransformer((document, context, _) =>
            {
                var terrabase = context.ApplicationServices.GetRequiredService<TerraBaseOptions>();
                document.Info.Version = ServiceVersion.Value;
                document.Servers = [new OpenApiServer { Url = "/", Description = $"TerraBase node in region {terrabase.Region}" }];
                document.Components ??= new OpenApiComponents();
                document.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
                document.Extensions["x-tagGroups"] = new JsonNodeExtension(JsonNode.Parse("""
                    [{"name":"Management","tags":["Databases","Imports","Backups","Indexes","Schema","Usage","Platform"]},
                     {"name":"Data","tags":["Collections","Documents","Queries","Transactions","Live","Functions","File storage","SQL"]},
                     {"name":"System","tags":["Health","Regions"]}]
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
                    foreach (var inferred in operation.Parameters.Where(value => value.In == parameter.In && value.Name == parameter.Name).ToArray())
                    {
                        operation.Parameters.Remove(inferred);
                    }

                    operation.Parameters.Add(parameter);
                }

                var policies = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>()
                    .Select(data => data.Policy).OfType<string>().ToList();
                if (policies.Count == 0)
                {
                    return;
                }

                if (policies.Contains(PlatformExplore.PolicyName))
                {
                    operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(TsAuthScheme, context.Document)] = [] }];
                    operation.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
                    operation.Extensions["x-required-claims"] = new JsonNodeExtension(new JsonArray(JsonValue.Create($"{PlatformExplore.RoleClaim}={PlatformExplore.AdminRole}")));
                    operation.Description += $"\n\nRequires a ts-auth user access token whose {PlatformExplore.RoleClaim} claim is {PlatformExplore.AdminRole}.";
                    operation.Responses ??= [];
                    var refusal = await context.GetOrCreateSchemaAsync(typeof(ErrorBody), cancellationToken: cancellationToken);
                    operation.Responses.TryAdd("401", Error("Unauthorized: missing, unknown or expired credential.", refusal));
                    operation.Responses.TryAdd("403", Error("Forbidden: not a platform admin.", refusal));
                    return;
                }

                var scopes = policies.Where(scope => ApiKeyScopes.All.Contains(scope)
                    || ApiKeyScopes.ProjectTokenScopes.Contains(scope)).ToList();
                if (operation.OperationId == "explainSql" && policies.Contains(UserAuthExtensions.PolicyName))
                {
                    scopes.Add(ApiKeyScopes.DatabasesRead);
                }

                if (scopes.Count == 0)
                {
                    return;
                }

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
        }).AddDocumentTransformer((document, _, _) =>
        {
            foreach (var path in document.Paths.Values)
            {
                if (path.Operations is not { } operations)
                {
                    continue;
                }
                foreach (var operation in operations.Values)
                {
                    ApiExamples.ApplyRequestExamples(operation);
                }
            }
            return Task.CompletedTask;
        }));

    private static OpenApiResponse Error(string description, IOpenApiSchema schema) => new()
    {
        Description = description,
        Content = new Dictionary<string, IOpenApiMediaType>(StringComparer.Ordinal)
        {
            ["application/json"] = new OpenApiMediaType { Schema = schema },
        },
    };
}
