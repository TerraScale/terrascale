using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.OpenApi;
using Microsoft.Net.Http.Headers;
using Scalar.AspNetCore;
using TerraScale.Dashboard.Platform.Assets;

namespace TerraScale.Dashboard.Platform.Contracts
{
    public static class DashboardContracts
    {
        public static IServiceCollection AddDashboardContracts(this IServiceCollection services)
        {
            services.AddOpenApi("integration", options =>
            {
                options.ShouldInclude = description => description.GroupName == "integration";
                options.AddDocumentTransformer((document, _, _) =>
                {
                    document.Info.Title = "TerraScale Dashboard integration API";
                    document.Info.Version = "v1";
                    document.Servers = [new OpenApiServer { Url = "https://dash.terrascale.tech" }];
                    document.Info.Description = "Same-origin dashboard APIs. Existing /api/v1 browser paths are preserved. " +
                        "New public APIs use /v1. Mutations require the current session and the _csrf form token " +
                        "bound to the same origin; authentication and membership checks run before actions. " +
                        "This document covers explicitly annotated integration routes, not every UI page.";
                    document.Components ??= new();
                    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                    document.Components.SecuritySchemes["dashboardSession"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.ApiKey,
                        In = ParameterLocation.Cookie,
                        Name = "ts-dashboard-session",
                        Description = "Server-managed HttpOnly session cookie. Obtain by signing in through ts-auth's hosted pages (GET /login)."
                    };
                    foreach (var path in document.Paths.Values)
                    {
                        foreach (var operation in path.Operations!.Values)
                        {
                            operation.Security = [new OpenApiSecurityRequirement
                            {
                                [new OpenApiSecuritySchemeReference("dashboardSession", document)] = []
                            }];
                        }
                    }

                    return Task.CompletedTask;
                });
            });
            return services;
        }

        public static IEndpointRouteBuilder MapDashboardContracts(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapOpenApi();
            endpoints.MapGet("/reference/terrascale.js", ReferenceScript)
                .ExcludeFromDescription();
            var reference = (RouteGroupBuilder)endpoints.MapScalarApiReference("/reference", (options, http) =>
            {
                var nonce = System.Net.WebUtility.HtmlEncode((string)http.Items["nonce"]!);
                var assets = http.RequestServices.GetRequiredService<AssetUrls>();
                options.AddDocument("integration", "Dashboard integration")
                    .WithTitle("TerraScale API reference")
                    .WithFavicon(assets.Url("brand/logo-icon-blue.svg"))
                    .WithBundleUrl("/reference/terrascale.js")
                    // Fixed product design; these are Scalar's vendor APIs, not runtime switches.
                    .ForceDarkMode().HideDarkModeToggle().WithTheme(ScalarTheme.None)
                    .DisableDefaultFonts() // Use the dashboard's self-hosted Geist faces under font-src self.
                    .WithCustomCss(ReferenceCss)
                    .AddHeadContent($"<meta property=\"csp-nonce\" content=\"{nonce}\">" +
                        $"<link rel=\"stylesheet\" href=\"{assets.Url("css/terrascale.css")}\">")
                    .WithNonce((string)http.Items["nonce"]!);
            });
            // Scalar 2.18.0 has no document-language setting. Fix the actual HTTP document as well as the DOM.
            reference.AddEndpointFilter(async (context, next) =>
            {
                var result = await next(context);
                return result is ContentHttpResult { ContentType: "text/html", ResponseContent: { } html }
                    ? Results.Content(html.Replace("<html>", "<html lang=\"en\">", StringComparison.Ordinal), "text/html")
                    : result;
            });
            return endpoints;
        }

        private static IResult ReferenceScript(HttpContext context)
        {
            var encodings = context.Request.GetTypedHeaders().AcceptEncoding;
            var requested = encodings?.FirstOrDefault(value => value.Value.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                ?? encodings?.FirstOrDefault(value => value.Value == "*");
            var gzip = requested is not null && (requested.Quality ?? 1) > 0;
            var identity = encodings?.FirstOrDefault(value => value.Value.Equals("identity", StringComparison.OrdinalIgnoreCase))
                ?? encodings?.FirstOrDefault(value => value.Value == "*");
            context.Response.Headers.Vary = "Accept-Encoding";
            if (!gzip && identity?.Quality == 0)
            {
                return Results.StatusCode(StatusCodes.Status406NotAcceptable);
            }

            var asset = gzip ? ScalarBundle.Value.Gzip : ScalarBundle.Value.Identity;
            // The script reads the current HTML nonce; its bytes contain no per-request data.
            context.Response.Headers.CacheControl = "public, no-cache";
            if (gzip)
            {
                context.Response.Headers.ContentEncoding = "gzip";
            }

            return Results.Bytes(asset.Bytes, "text/javascript", entityTag: asset.Tag);
        }

        private sealed record ReferenceAsset(byte[] Bytes)
        {
            public EntityTagHeaderValue Tag { get; } = new($"\"{Convert.ToHexStringLower(SHA256.HashData(Bytes))}\"");
        }

        private static readonly Lazy<(ReferenceAsset Identity, ReferenceAsset Gzip)> ScalarBundle = new(() =>
        {
            // Scalar's official csp-nonce meta supports its main CSS only. Its bundled Sonner,
            // CodeMirror and Vue theme style producers need the same nonce before insertion.
            // Adapt only the pinned package asset, never document/DOM prototypes or untrusted markup.
            using var resource = typeof(ScalarOptions).Assembly.GetManifestResourceStream("ScalarStaticAssets.scalar.js")
                ?? throw new InvalidOperationException("Scalar bundle missing");
            var gzip = resource.ReadByte() == 0x1f && resource.ReadByte() == 0x8b;
            resource.Position = 0;
            using var decoded = gzip ? (Stream)new GZipStream(resource, CompressionMode.Decompress) : resource;
            using var reader = new StreamReader(decoded);
            var script = reader.ReadToEnd();
            const string nonce = "document.head.querySelector('meta[property=csp-nonce]').content";
            var producers = new Regex(@"\b(?:document|[a-zA-Z_$][\w$]*)\.createElement\(`style`\)", RegexOptions.CultureInvariant);
            const string theme = "P(Mr(`style`),{innerHTML:";
            // Select Zod's existing interpreter without its forbidden Function() capability probe.
            const string evalProbe = "try{return Function(``),!0}catch{return!1}";
            // Desktop and mobile SearchButton instances both listen on window. Handle one shortcut once.
            const string searchShortcut = "t.key===e.searchHotKey&&(t.preventDefault(),t.stopPropagation(),n.open?n.hide():n.show())";
            if (producers.Matches(script).Count != 4 || script.Split(theme, StringSplitOptions.None).Length != 2 ||
                script.Split(evalProbe, StringSplitOptions.None).Length != 2 ||
                script.Split(searchShortcut, StringSplitOptions.None).Length != 2)
            {
                throw new InvalidOperationException("Scalar style producers changed; review CSP adapter before upgrading");
            }

            script = producers.Replace(script, match => $"Object.assign({match.Value},{{nonce:{nonce}}})");
            script = script.Replace(theme, $"P(Mr(`style`),{{nonce:{nonce},innerHTML:", StringComparison.Ordinal)
                .Replace(evalProbe, "return!1", StringComparison.Ordinal)
                .Replace(searchShortcut, searchShortcut.Replace("t.stopPropagation()", "t.stopImmediatePropagation()", StringComparison.Ordinal), StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(script);
            using var compressed = new MemoryStream();
            using (var writer = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                writer.Write(bytes);
            }

            return (new(bytes), new(compressed.ToArray()));
        });

        private const string ReferenceCss = """
        html { color-scheme: dark; }
        body { background: var(--ts-canvas); color: var(--ts-ink); }
        .dark-mode {
          --scalar-background-1: var(--ts-canvas);
          --scalar-background-2: var(--ts-surface);
          --scalar-background-3: var(--ts-surface-2);
          --scalar-background-accent: var(--ts-accent-soft);
          --scalar-color-1: var(--ts-ink);
          --scalar-color-2: var(--ts-ink-2);
          --scalar-color-3: var(--ts-ink-3);
          --scalar-color-accent: var(--ts-link);
          --scalar-border-color: var(--ts-line-strong);
          --scalar-button-1: var(--ts-accent-fill);
          --scalar-button-1-hover: var(--ts-accent-fill-hover);
          --scalar-button-1-color: var(--ts-on-accent);
          --scalar-font: var(--ts-font-sans);
          --scalar-font-code: var(--ts-font-mono);
          --scalar-sidebar-background-1: var(--ts-canvas);
          --scalar-sidebar-color-1: var(--ts-ink);
          --scalar-sidebar-color-2: var(--ts-ink-2);
          --scalar-sidebar-color-active: var(--ts-link);
          --scalar-sidebar-item-hover-color: var(--ts-ink);
          --scalar-sidebar-item-hover-background: var(--ts-hover);
          --scalar-sidebar-item-active-background: var(--ts-accent-soft);
          --scalar-sidebar-border-color: var(--ts-line-strong);
        }
        :is(a, button, input, select, textarea, summary, [tabindex="0"],
            [role="button"], [role="tab"], [role="menuitem"], [role="option"],
            [role="checkbox"], [role="radio"], [role="switch"]) {
          min-width: 24px; min-height: 24px;
        }
        a { display: inline-flex; align-items: center; }
        :focus-visible { outline: 2px solid var(--ts-focus) !important; outline-offset: 2px; }
        """;
    }
}
