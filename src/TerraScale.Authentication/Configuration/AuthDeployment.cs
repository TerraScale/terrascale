namespace TerraScale.Authentication.Configuration;

public abstract record class AuthDeployment(string Name, Uri PublicOrigin, string Authority, string DashboardOrigin, string PasskeyRpId)
{
    private static readonly IReadOnlyList<string> ProductionRegions = ["gru", "sjc", "fra"];

    public virtual IReadOnlyList<string> Regions => ProductionRegions;

    private const string EmailVariables = "TS_AUTH_SMTP_HOST, TS_AUTH_SMTP_USERNAME, TS_AUTH_SMTP_PASSWORD, CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_EMAIL_TOKEN";

    public static AuthDeployment Of(Uri publicOrigin, bool development) => development
        ? new DevelopmentDeployment(publicOrigin)
        : publicOrigin.AbsoluteUri.TrimEnd('/') switch
        {
            ProductionDeployment.Origin => new ProductionDeployment(),
            StagingDeployment.Origin => new StagingDeployment(),
            _ => throw new AuthConfigurationException(
                $"TS_AUTH_PUBLIC_ORIGIN must be {ProductionDeployment.Origin} or {StagingDeployment.Origin} outside Development"),
        };

    public abstract string AdmittedRegion(string? region);

    public abstract void ValidateEmail(AuthOptions options);

    private protected string RequiredRegion(string? region) =>
        region ?? throw new AuthConfigurationException($"TS_REGION is required in {this.Name}");

    private protected static void RequireEmail(AuthOptions options)
    {
        if (options.Smtp is null || options.CloudflareEmail is null)
        {
            throw new AuthConfigurationException($"{EmailVariables} are required in production");
        }
    }

    private protected static void RefuseEmail(AuthOptions options)
    {
        if (options.Smtp is not null || options.CloudflareEmail is not null)
        {
            throw new AuthConfigurationException($"Staging sends no email: {EmailVariables} must be unset");
        }
    }
}

public sealed record class ProductionDeployment() : AuthDeployment("production", new(Origin), "gru", "https://dash.terrascale.tech", "terrascale.tech")
{
    public const string Origin = "https://auth.terrascale.tech";

    public override string AdmittedRegion(string? region) => this.RequiredRegion(region);

    public override void ValidateEmail(AuthOptions options) => RequireEmail(options);
}

public sealed record class StagingDeployment() : AuthDeployment("staging", new(Origin), "stg", "https://stg.dash.terrascale.tech", "terrascale.tech")
{
    public const string Origin = "https://stg.auth.terrascale.tech";

    public override IReadOnlyList<string> Regions { get; } = ["stg"];

    public override string AdmittedRegion(string? region) => this.RequiredRegion(region);

    public override void ValidateEmail(AuthOptions options) => RefuseEmail(options);
}

public sealed record class DevelopmentDeployment(Uri PublicOrigin)
    : AuthDeployment("development", PublicOrigin, "gru", "https://dash.terrascale.tech", App.PasskeyRpId(PublicOrigin.Host))
{
    public override string AdmittedRegion(string? region) => region ?? this.Authority;

    public override void ValidateEmail(AuthOptions options)
    {
    }
}
