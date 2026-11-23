using System.Text.Json;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

public readonly union MethodsInput(TsAuthProjectMethods, DashboardValidation);

public readonly union BrandingInput(TsAuthProjectBranding, DashboardValidation);

public readonly union ProviderInput(TsAuthProjectProviderUpdate, DashboardValidation);

public readonly union NewClientInput(TsAuthProjectCreateOAuthClient, DashboardValidation);

public readonly union WebhookInput(TsAuthCreateWebhookEndpoint, DashboardValidation);

public readonly union ClientUpdateInput(TsAuthProjectClientUpdate, DashboardValidation);

public readonly union ConfigurationInput(TsAuthProjectConfiguration, DashboardValidation);

public readonly union MetadataInput(JsonElement, DashboardValidation);

internal sealed record MachinePolicyResult(IReadOnlyList<string> Scopes, IReadOnlyList<string> Audiences, int Minutes);

internal readonly union MachinePolicyInput(MachinePolicyResult, DashboardValidation);
