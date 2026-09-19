namespace TerraScale.Auth;

/// <summary>Every feature slice registers its endpoints here. One file per
/// feature module; App.Configure delegates to these.</summary>
public static class EndpointModules
{
    /// <summary>Maps all feature endpoints onto the application.</summary>
    public static void MapAll(WebApplication app)
    {
        Users.UserEndpoints.Map(app);
        OAuth.OAuthEndpoints.Map(app);
        Passkeys.PasskeyEndpoints.Map(app);
        Mfa.MfaEndpoints.Map(app);
        Organizations.OrganizationEndpoints.Map(app);
        Management.ManagementEndpoints.Map(app);
        Webhooks.WebhookEndpointsModule.Map(app);
    }
}
