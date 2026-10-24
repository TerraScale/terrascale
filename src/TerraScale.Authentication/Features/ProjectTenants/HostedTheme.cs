using System.Text.Json;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary>
/// What a tenant's hosted pages show and allow: its brand and its sign-in methods. The platform tenant has the
/// TerraScale brand and every method; a project has its settings (project-tenant-management.md), named after the
/// project unless it set a name.
/// </summary>
/// <param name="Name">The brand name, or null for TerraScale.</param>
/// <param name="LogoUrl">An https logo, or null.</param>
/// <param name="ThemeColor"><c>#rrggbb</c>, or null.</param>
/// <param name="Passcode">Email passcode sign-in.</param>
/// <param name="Password">Password sign-in and reset.</param>
/// <param name="SignUp">Self sign-up.</param>
/// <param name="TitleSize">Hosted title size in pixels.</param>
/// <param name="DefaultTheme">Light, dark or system appearance.</param>
/// <param name="FooterLinks">Project footer links.</param>
/// <param name="BackgroundColor">Optional page background colour.</param>
/// <param name="TextColor">Optional text colour.</param>
public sealed record HostedTheme(string? Name = null, string? LogoUrl = null, string? ThemeColor = null, bool Passcode = true, bool Password = true, bool SignUp = true,
    int? TitleSize = null, string? DefaultTheme = null, IReadOnlyList<FooterLinkBody>? FooterLinks = null, string? BackgroundColor = null, string? TextColor = null)
{
    /// <summary>The configured social sign-in providers, in <see cref="SocialProviders.Names"/> order.</summary>
    public IReadOnlyList<string> Providers { get; init; } = [];

    internal const string ItemKey = "ts-auth:theme";

    /// <summary>The platform's pages.</summary>
    public static readonly HostedTheme Platform = new();

    /// <summary>The theme of the request's hosted page.</summary>
    public static HostedTheme Of(HttpContext context) => context?.Items[ItemKey] as HostedTheme ?? Platform;
}

/// <summary>Resolves each hosted page's <see cref="HostedTheme"/>.</summary>
public static class HostedThemes
{
    /// <summary>Loads a project page's theme after <see cref="Tenancy"/> resolved its tenant, read fresh on every page.</summary>
    public static void UseHostedTheme(this WebApplication app) => app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        if (context.Request.Path.StartsWithSegments("/ui") && Tenancy.Resolved(context) is { } tenant && Tenancy.IsProject(tenant))
        {
            var services = context.RequestServices;
            var settings = await services.GetRequiredService<ProjectAuthSettings>().GetAsync(tenant, context.RequestAborted);
            var project = (await services.GetRequiredService<AuthStore>().GetAsync(AuthStore.ProjectCollection, OidcIssuer.InstanceId, tenant, context.RequestAborted))
                ?.Document.Deserialize(PlatformJson.Default.ProjectRecord);
            context.Items[HostedTheme.ItemKey] = new HostedTheme(settings.Name ?? project?.Name, settings.LogoUrl, settings.ThemeColor, settings.Passcode, settings.Password, settings.SignUp,
                settings.TitleSize, settings.DefaultTheme, settings.FooterLinks, settings.BackgroundColor, settings.TextColor)
            {
                Providers = [.. SocialProviders.Names.Where(name => settings.Provider(name) is not null)],
            };
        }

        await next(context);
    });
}
