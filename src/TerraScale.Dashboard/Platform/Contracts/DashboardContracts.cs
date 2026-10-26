using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace TerraScale.Dashboard.Platform.Contracts;

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
                    foreach (var operation in path.Operations!.Values)
                        operation.Security = [new OpenApiSecurityRequirement
                        {
                            [new OpenApiSecuritySchemeReference("dashboardSession", document)] = []
                        }];
                return Task.CompletedTask;
            });
        });
        return services;
    }

    public static IEndpointRouteBuilder MapDashboardContracts(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOpenApi();
        endpoints.MapScalarApiReference("/reference", (options, http) => options
            .AddDocument("integration", "Dashboard integration")
            .WithNonce((string)http.Items["nonce"]!));
        return endpoints;
    }
}
