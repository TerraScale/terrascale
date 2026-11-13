using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Ui;

public sealed record WaitlistPageModel(string Tenant, HostedTheme Theme, string Nonce, bool Received = false)
{
    public HostedLayoutModel Layout => new("Join the waitlist", this.Nonce, this.Theme.ThemeColor, this.Theme.LogoUrl, this.Theme.Name,
        this.Theme.TitleSize, this.Theme.FooterLinks?.Select(link => (link.Label, link.Url)).ToArray(), this.Theme.DefaultTheme, this.Tenant,
        this.Theme.BackgroundColor, this.Theme.TextColor);
}
