using System.Text.Json;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed record HostedTheme(string? Name = null, string? LogoUrl = null, string? ThemeColor = null, bool Passcode = true, bool Password = true, string SignUpMode = "public",
    int? TitleSize = null, string? DefaultTheme = null, IReadOnlyList<FooterLinkBody>? FooterLinks = null, string? BackgroundColor = null, string? TextColor = null)
{
    public IReadOnlyList<string> Providers { get; init; } = [];

    public IReadOnlyList<string> RedirectUrls { get; init; } = [];

    internal const string ItemKey = "ts-auth:theme";

    public static readonly HostedTheme Platform = new();

    public static HostedTheme Of(HttpContext context) => context?.Items[ItemKey] as HostedTheme ?? Platform;
}

public static class HostedThemes
{
    public static void UseHostedTheme(this WebApplication app) => app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        if (context.Request.Path.StartsWithSegments("/ui") && Tenancy.Resolved(context) is { } tenant && Tenancy.IsProject(tenant))
        {
            var services = context.RequestServices;
            var settings = await services.GetRequiredService<ProjectAuthSettings>().GetAsync(tenant, context.RequestAborted);
            var project = await services.GetRequiredService<AuthData>().ReadAsync((db, token) => db.Projects.SingleOrDefaultAsync(row => row.Id == tenant, token), context.RequestAborted);
            context.Items[HostedTheme.ItemKey] = new HostedTheme(settings.Name ?? project?.Name, settings.LogoUrl, settings.ThemeColor, settings.Passcode, settings.Password, settings.SignUpMode,
                settings.TitleSize, settings.DefaultTheme, settings.FooterLinks, settings.BackgroundColor, settings.TextColor)
            {
                RedirectUrls = settings.Configuration?.RedirectUrls ?? [],
                Providers = [.. SocialProviders.Names.Where(name => settings.Provider(name) is not null)],
            };
        }

        if (context.Request.Path.StartsWithSegments("/ui") && Tenancy.Resolved(context) == OidcIssuer.InstanceId)
        {
            context.Items[HostedTheme.ItemKey] = HostedTheme.Platform with { Providers = context.RequestServices.GetRequiredService<Configuration.AuthOptions>().SocialClients.Select(provider => provider.Provider).ToArray() };
        }

        await next(context);
    });
}
