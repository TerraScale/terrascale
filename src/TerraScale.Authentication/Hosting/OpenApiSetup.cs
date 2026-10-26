using System.Text.RegularExpressions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using TerraScale.Authentication.Configuration;

namespace TerraScale.Authentication.Hosting;

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

    /// <summary>ts-alerts noncritical email admission credential.</summary>
    public const string MailAdmission = "mailAdmission";

    /// <summary>OAuth client authentication with HTTP Basic.</summary>
    public const string ClientBasic = "clientBasic";

    /// <summary>Directory or organization bearer token (SCIM, organizations).</summary>
    public const string BearerAuth = "bearerAuth";

    /// <summary>A platform user's own sign-in access token, for the platform management API (TS-598).</summary>
    public const string PlatformUser = "platformUser";

    /// <summary>A whole project API key, restricted to server-to-server end-user token minting.</summary>
    public const string ProjectApiKey = "projectApiKey";
    public const string ApiKeyToken = "apiKeyToken";

    /// <summary>Status-only account deletion capability, independent of sign-in credentials.</summary>
    public const string AccountDeletionReceipt = "accountDeletionReceipt";

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
            .Configure<ScalarOptions>(scalar => scalar.DisableDefaultFonts().WithNonce())
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

                if (operation.OperationId is "oauth_token" or "oauth_revoke")
                {
                    // OpenAPI cannot express credentials in a form body as a security scheme.
                    // The empty alternative covers public clients and client_secret_post, not
                    // anonymous grants: OpenIddict still validates the registered client.
                    operation.Security = [Requirement(SecuritySchemes.ClientBasic, context.Document), new OpenApiSecurityRequirement()];
                    operation.Description += " Public clients send client_id without a client secret; confidential clients authenticate with client_secret_basic (HTTP Basic) or client_secret_post (form client_id and client_secret). The empty OpenAPI security alternative represents requests without an Authorization header, not a bypass of client validation or grant permissions. Never send a confidential client's secret from a browser.";
                }

                if (operation.OperationId == "oauth_token")
                {
                    operation.Description += " A refresh scope subset limits the new access token, not the successor refresh token's original authority (RFC 6749 section 6); omitted scope requests the original set, and expanded or unknown scopes are rejected. The response scope may be omitted when equal to the explicit requested set. Rotation never extends the family's absolute expiry and can stop earlier at its ancestry proof budget; sign in again on invalid_grant. See docs/auth/regional/refresh-scope-lifetime.md for the scope and lifetime contract.";
                }

                if (operation.OperationId is "delete_account" or "account_deletion_status")
                {
                    (operation.Parameters ??= []).Add(new OpenApiParameter
                    {
                        Name = "X-TerraScale-Deletion-Receipt",
                        In = ParameterLocation.Header,
                        Required = operation.OperationId == "account_deletion_status",
                        Description = "Status-only capability: 32 random bytes in canonical unpadded base64url. Never put it in a URL or logs.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 43, MaxLength = 43, Pattern = "^[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$" },
                    });
                }

                if (operation.OperationId == "account_deletion_status")
                {
                    (operation.Parameters ??= []).Add(new OpenApiParameter
                    {
                        Name = "organization",
                        In = ParameterLocation.Query,
                        Required = false,
                        Description = "Candidate organization ULID; completion additionally requires its own deletion fence.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[0-7][0-9A-HJKMNP-TV-Z]{25}$" },
                    });
                }

                if (operation.OperationId == "export_project_users")
                {
                    (operation.Parameters ??= []).Add(new OpenApiParameter
                    {
                        Name = "limit",
                        In = ParameterLocation.Query,
                        Description = "Rows per page, default 100.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "100" },
                    });
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "after",
                        In = ParameterLocation.Query,
                        Description = "The prior next cursor, unchanged; omit to start.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 8192 },
                    });
                    (operation.Parameters ??= []).Add(new OpenApiParameter
                    {
                        Name = "format",
                        In = ParameterLocation.Query,
                        Description = "json (default) or csv. CSV linked_providers is a JSON array; formula-leading cells have an apostrophe prefix.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^(json|csv)$" },
                    });
                    if (operation.Responses!["200"] is OpenApiResponse response)
                    {
                        response.Content!["text/csv"] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
                        response.Headers = new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal)
                        {
                            ["X-Export-Next"] = new OpenApiHeader { Description = "Opaque continuation; absent on the last page.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } },
                            ["X-Export-Created-Before"] = new OpenApiHeader { Description = "Fixed creation cutoff, not a transaction snapshot.", Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" } },
                        };
                    }
                }

                // OpenAPI 11 documents every reference-typed response body as oneOf [null, $ref]. No response body
                // is null, so the plain $ref that the contract has always served is restored.
                foreach (var response in operation.Responses?.Values ?? Enumerable.Empty<IOpenApiResponse>())
                {
                    foreach (var media in response.Content?.Values ?? Enumerable.Empty<IOpenApiMediaType>())
                    {
                        if (media is OpenApiMediaType { Schema.OneOf: [{ Type: JsonSchemaType.Null }, var reference] } concrete)
                        {
                            concrete.Schema = reference;
                        }
                    }
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
        ["Sign-in"] = "Hosted email passcode and verified-provider sign-in pages.",
        ["Account"] = "The signed-in platform account's linked sign-in providers and reauthenticated unlinking.",
        ["Security"] = "Hosted account security pages: authenticator (TOTP) enrolment, recovery codes and removal.",
        ["Email"] = "Email branding, copy, previews, test sends and version history, for the ts-dashboard client (HTTP Basic).",
        ["Organizations"] = "Organizations of the terrascale tenant (TS-598). The caller is a signed-in platform user.",
        ["Members"] = "Organization members and their organization roles (viewer, member, admin, owner).",
        ["Projects"] = "Projects of an organization. Each project is also the tenant of its end users.",
        ["Project members"] = "Explicit project roles (viewer, member, admin), for organization members and collaborators.",
        ["API keys"] = "Client credentials owned by an organization or project, exchanged at the token endpoint for short-lived tokens.",
        ["Invitations"] = "Email invitations to an organization role or to one project's role.",
        ["Ownership transfer"] = "Handing an organization's ownership to another member, confirmed by the nominee.",
        ["Audit"] = "Audit events of an organization's identity changes.",
        ["Project tenants"] = "A project tenant's end users, live sessions, OAuth clients and sign-in settings, for the project's platform members (viewer reads, admin changes).",
        ["Single sign-on"] = "An organization's OpenID Connect identity provider and the email domain it proves by DNS: admins read the connection, owners change, verify and delete it.",
    };

    private static readonly Dictionary<string, Func<OpenApiSecurityScheme>> SchemeDefinitions = new(StringComparer.Ordinal)
    {
        [SecuritySchemes.AccountDeletionReceipt] = () => new() { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-TerraScale-Deletion-Receipt" },
        [SecuritySchemes.ManagementBearer] = () => new() { Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "Instance management API key (sk_…)." },
        [SecuritySchemes.SessionCookie] = () => new() { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = SecuritySchemes.SessionCookieName },
        [SecuritySchemes.OAuthAccessBearer] = () => new() { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" },
        [SecuritySchemes.OperatorBearer] = () => new() { Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "Operator bootstrap key." },
        [SecuritySchemes.MailAdmission] = () => new() { Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "Domain-separated ts-alerts admission credential; no dashboard or authentication authority." },
        [SecuritySchemes.ClientBasic] = () => new() { Type = SecuritySchemeType.Http, Scheme = "basic" },
        [SecuritySchemes.BearerAuth] = () => new() { Type = SecuritySchemeType.Http, Scheme = "bearer" },
        [SecuritySchemes.ProjectApiKey] = () => new()
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "Whole project API key, with no database restriction and both documents:read and documents:write. Backend use only; never expose this key to a browser. Organization keys and JWTs are refused.",
        },
        [SecuritySchemes.ApiKeyToken] = () => new()
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "An access token the token endpoint issued for an API key (client credentials), carrying key_id and key_gen.",
        },
        [SecuritySchemes.PlatformUser] = () => new()
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "A sign-in access token of the terrascale tenant (typ at+jwt, aud including the issuer), with the user's current authentication epoch. Primary-only tokens for active MFA accounts are refused; unavailable or corrupt MFA state fails closed. Organization, project, API key and service tokens are refused.",
        },
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
                if (path is "/{slug}/oauth/token" or "/ui/{slug}/sign-in" or "/ui/{slug}/sign-in/verify" or "/v1/terrascale/projects/{project}/end-user-tokens"
                    && operation.Responses.TryGetValue("429", out var limited) && limited is OpenApiResponse response)
                {
                    (response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal))["Retry-After"] = new OpenApiHeader
                    {
                        Description = "Minimum delay in whole seconds before retrying a rate-limited request.",
                        Required = true,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" },
                    };
                }

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
