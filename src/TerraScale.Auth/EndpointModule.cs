using TerraScale.Endpoints;

namespace TerraScale.Auth;

/// <summary>Composes the endpoint catalog for all feature slices.</summary>
internal static class EndpointModules
{
    internal static IReadOnlyList<Type> All =>
    [
        .. Admin.AdminModule.Endpoints,
        .. Billing.BillingModule.Endpoints,
        .. Connections.ConnectionsModule.Endpoints,
        .. Instances.InstancesModule.Endpoints,
        .. Management.ManagementModule.Endpoints,
        .. Mfa.MfaModule.Endpoints,
        .. OAuth.OAuthModule.Endpoints,
        .. Observability.ObservabilityModule.Endpoints,
        .. Organizations.OrganizationsModule.Endpoints,
        .. Passcodes.PasscodesModule.Endpoints,
        .. Passkeys.PasskeysModule.Endpoints,
        .. Scim.ScimModule.Endpoints,
        .. Templates.TemplatesModule.Endpoints,
        .. Users.UsersModule.Endpoints,
        .. Webhooks.WebhooksModule.Endpoints,
    ];

    internal static void MapAll(WebApplication app) => app.MapTerraScaleEndpoints();
}
