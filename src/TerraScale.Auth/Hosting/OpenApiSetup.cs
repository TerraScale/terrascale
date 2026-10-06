using System.Text.RegularExpressions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TerraScale.Auth.Configuration;

namespace TerraScale.Auth.Hosting;

/// <summary>Security schemes shared by the ts-auth API surface.</summary>
public static class SecuritySchemes
{
    /// <summary>Instance management key (<c>Authorization: Bearer sk_…</c>) for <c>/v1/{slug}/*</c>.</summary>
    public const string ManagementBearer = "ManagementBearer";

    /// <summary>Hosted session cookie for <c>/ui/{slug}/*</c> pages.</summary>
    public const string SessionCookie = "SessionCookie";

    /// <summary>OAuth access token issued by the instance.</summary>
    public const string OAuthAccessBearer = "OAuthAccessBearer";

    /// <summary>Operator bootstrap key for <c>/v1/instances</c>.</summary>
    public const string OperatorBearer = "OperatorBearer";

    /// <summary>OAuth client authentication with HTTP Basic.</summary>
    public const string ClientBasic = "clientBasic";

    /// <summary>Directory or organization bearer token (SCIM, organizations).</summary>
    public const string BearerAuth = "bearerAuth";

    /// <summary>Session cookie name used by every hosted UI route.</summary>
    public const string SessionCookieName = "tsauth_session";
}

/// <summary>Declares an endpoint's security scheme in the OpenAPI document. It must match the server-side check.</summary>
/// <param name="Scheme">One of the <see cref="SecuritySchemes"/> names.</param>
public sealed record RequiresSecurity(string Scheme);

/// <summary>
/// Completes the generated OpenAPI document:
/// security schemes, per-route security that matches enforcement, and a default response
/// for operations that declare none.
/// </summary>
public static partial class OpenApiSetup
{
    /// <summary>
    /// Adds ts-auth's transformers to the <c>v1</c> document registered by TerraScale.Endpoints,
    /// and keeps Scalar self-hosted (no third-party font requests).
    /// </summary>
    public static IServiceCollection AddAuthOpenApi(this IServiceCollection services) =>
        services
            .Configure<Scalar.AspNetCore.ScalarOptions>(scalar => scalar.DefaultFonts = false)
            .Configure<OpenApiOptions>("v1", options =>
        {
            options.AddOperationTransformer((operation, context, _) =>
            {
                // Same precedence as ASP.NET authorization: AllowAnonymous anywhere wins;
                // otherwise the most specific (last) declaration, the endpoint's own over its
                // group's, decides.
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                if (metadata.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any())
                {
                    // Explicitly anonymous: an empty list, never replaced by a path default.
                    operation.Security = [];
                }
                else if (metadata.OfType<RequiresSecurity>().LastOrDefault() is { } declared)
                {
                    operation.Security = [Requirement(declared.Scheme, context.Document)];
                }

                return Task.CompletedTask;
            });
            options.AddDocumentTransformer((document, context, _) =>
            {
                Complete(document, context.ApplicationServices.GetRequiredService<AuthOptions>().PublicOrigin);
                return Task.CompletedTask;
            });
        });

    // Descriptions for every tag the API uses; add an entry with each new feature tag.
    private static readonly Dictionary<string, string> TagDescriptions = new(StringComparer.Ordinal)
    {
        ["Observability"] = "Liveness and readiness probes.",
        ["Admin"] = "Hosted administrator portal and its bundled assets.",
        ["OAuth"] = "OpenID Connect provider for the issuer {public origin}/{slug} (authorization code with S256 PKCE, rotating refresh tokens).",
        ["Sign-in"] = "Hosted email passcode sign-in pages.",
        ["Headless"] = "Headless authentication for first-party confidential clients (ts-dashboard): sign-up, emailed codes and password reset; tokens come from the token endpoint's headless grants.",
    };

    private static readonly Dictionary<string, Func<OpenApiSecurityScheme>> SchemeDefinitions = new(StringComparer.Ordinal)
    {
        [SecuritySchemes.ManagementBearer] = () => new() { Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "Instance management API key (sk_…)." },
        [SecuritySchemes.SessionCookie] = () => new() { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = SecuritySchemes.SessionCookieName },
        [SecuritySchemes.OAuthAccessBearer] = () => new() { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" },
        [SecuritySchemes.OperatorBearer] = () => new() { Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "Operator bootstrap key." },
        [SecuritySchemes.ClientBasic] = () => new() { Type = SecuritySchemeType.Http, Scheme = "basic" },
        [SecuritySchemes.BearerAuth] = () => new() { Type = SecuritySchemeType.Http, Scheme = "bearer" },
    };

    private static void Complete(OpenApiDocument document, Uri publicOrigin)
    {
        // The configured public origin, never the request's Host header.
        document.Servers = [new OpenApiServer { Url = publicOrigin.GetLeftPart(UriPartial.Path).TrimEnd('/') }];
        document.Info.License = new OpenApiLicense { Name = "Apache-2.0", Identifier = "Apache-2.0" };

        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (path, item) in document.Paths)
        {
            if (item.Operations is null)
            {
                continue;
            }

            foreach (var operation in item.Operations.Values)
            {
                // Endpoints that read a route value from the request (rather than binding it) still take it in the path.
                foreach (var name in RouteParameter().Matches(path).Select(match => match.Groups["name"].Value))
                {
                    if (operation.Parameters?.Any(parameter => parameter.In == ParameterLocation.Path && parameter.Name == name) != true)
                    {
                        (operation.Parameters ??= []).Add(new OpenApiParameter
                        {
                            Name = name,
                            In = ParameterLocation.Path,
                            Required = true,
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                        });
                    }
                }

                if (operation.Security is null)
                {
                    // Unspecified: the credential the route's prefix requires, or anonymous.
                    operation.Security = DefaultScheme(path) is { } scheme ? [Requirement(scheme, document)] : [new OpenApiSecurityRequirement()];
                }
                else if (operation.Security.Count == 0)
                {
                    // Declared anonymous; serialized as an explicit empty requirement.
                    operation.Security = [new OpenApiSecurityRequirement()];
                }

                foreach (var requirement in operation.Security)
                {
                    foreach (var reference in requirement.Keys.OfType<OpenApiSecuritySchemeReference>())
                    {
                        used.Add(reference.Reference.Id!);
                    }
                }

                operation.Responses ??= [];
                if (operation.Responses.Count == 0)
                {
                    operation.Responses["default"] = new OpenApiResponse
                    {
                        Description = "See the endpoint response status and body.",
                    };
                }
            }
        }

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = used.ToDictionary(
            name => name,
            name => (IOpenApiSecurityScheme)(SchemeDefinitions.TryGetValue(name, out var create)
                ? create()
                : throw new InvalidOperationException($"Undefined OpenAPI security scheme '{name}'.")),
            StringComparer.Ordinal);

        var tags = document.Paths.Values
            .SelectMany(item => item.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>())
            .SelectMany(operation => operation.Tags ?? new HashSet<OpenApiTagReference>())
            .Select(tag => tag.Name!)
            .ToHashSet(StringComparer.Ordinal);
        document.Tags = new HashSet<OpenApiTag>(tags.Order(StringComparer.Ordinal).Select(name => new OpenApiTag
        {
            Name = name,
            Description = TagDescriptions.TryGetValue(name, out var description)
                ? description
                : throw new InvalidOperationException($"Add a description for OpenAPI tag '{name}'."),
        }));
    }

    [GeneratedRegex(@"\{(?<name>[^{}]+)\}")]
    private static partial Regex RouteParameter();

    private static OpenApiSecurityRequirement Requirement(string scheme, OpenApiDocument? document) =>
        new() { [new OpenApiSecuritySchemeReference(scheme, document)] = [] };

    // Instance-scoped API routes, hosted admin pages and userinfo each have a distinct
    // credential; nothing else inherits one implicitly.
    private static string? DefaultScheme(string path) =>
        path.StartsWith("/v1/{slug}/", StringComparison.Ordinal) ? SecuritySchemes.ManagementBearer
        : path.StartsWith("/ui/{slug}/admin", StringComparison.Ordinal) ? SecuritySchemes.SessionCookie
        : path == "/{slug}/oauth/userinfo" ? SecuritySchemes.OAuthAccessBearer
        : path == "/v1/instances" ? SecuritySchemes.OperatorBearer
        : null;
}
