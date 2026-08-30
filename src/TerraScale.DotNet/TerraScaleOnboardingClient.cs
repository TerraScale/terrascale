using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Client for the resumable TerraScale onboarding workflow.
/// </summary>
public sealed class TerraScaleOnboardingClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleOnboardingClient(TerraScaleClient client) => this.client = client;

    /// <summary>
    /// Gets the current caller's resumable onboarding state.
    /// </summary>
    public Task<TerraScaleOnboardingState> GetStateAsync(CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleOnboardingState>(
            HttpMethod.Get,
            TerraScaleApiRoutes.OnboardingGetState(),
            body: null,
            cancellationToken);

    /// <summary>
    /// Creates the caller's first organization during onboarding.
    /// </summary>
    public Task<TerraScaleOnboardingState> CreateOrganizationAsync(
        TerraScaleCreateOnboardingOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        return client.SendAsync<TerraScaleOnboardingState>(
            HttpMethod.Post,
            TerraScaleApiRoutes.OnboardingOrganizationCreate(),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Creates the caller's first project and completes onboarding.
    /// </summary>
    public Task<TerraScaleOnboardingState> CreateProjectAsync(
        TerraScaleCreateOnboardingProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentNullException.ThrowIfNull(request.Regions);
        foreach (var region in request.Regions)
        {
            TerraScaleRegionCodes.Validate(region, nameof(request.Regions));
        }

        TerraScaleRegionCodes.Validate(request.BackupRegion, nameof(request.BackupRegion));
        return client.SendAsync<TerraScaleOnboardingState>(
            HttpMethod.Post,
            TerraScaleApiRoutes.OnboardingProjectCreate(),
            request,
            cancellationToken);
    }
}

/// <summary>
/// Request to create the first organization during onboarding.
/// </summary>
public sealed record TerraScaleCreateOnboardingOrganizationRequest(string Name);

/// <summary>
/// Request to create the first project during onboarding.
/// </summary>
public sealed record TerraScaleCreateOnboardingProjectRequest(
    string Name,
    IReadOnlyList<string> Regions,
    string BackupRegion);

/// <summary>
/// State of one onboarding step.
/// </summary>
public sealed record TerraScaleOnboardingStepState(
    [property: JsonRequired] string Key,
    [property: JsonRequired] string State);

/// <summary>
/// Resumable onboarding state returned by the public API.
/// </summary>
public sealed record TerraScaleOnboardingState(
    [property: JsonRequired] string UserId,
    [property: JsonRequired] string Email,
    [property: JsonRequired] string DisplayName,
    [property: JsonRequired] string PhoneNumber,
    [property: JsonRequired] bool AccountInformationCompleted,
    [property: JsonRequired] bool PaymentMethodRegistered,
    [property: JsonRequired] bool PaymentMethodValidated,
    [property: JsonRequired] string PaymentValidationStatus,
    [property: JsonRequired] string MfaDecision,
    [property: JsonRequired] string? OrganizationId,
    [property: JsonRequired] bool OrganizationOwnerAssigned,
    [property: JsonRequired] bool PaymentMethodLinkedToOrganization,
    [property: JsonRequired] bool SubscriptionOrTrialActive,
    [property: JsonRequired] string? ProjectId,
    [property: JsonRequired] bool ProjectOwnerAssigned,
    [property: JsonRequired] IReadOnlyList<string> ProjectPrimaryRegions,
    [property: JsonRequired] string ProjectBackupRegion,
    [property: JsonRequired] bool Completed,
    [property: JsonRequired] string Status,
    [property: JsonRequired] string NextStep,
    [property: JsonRequired] IReadOnlyList<TerraScaleOnboardingStepState> Steps,
    [property: JsonRequired] DateTimeOffset UpdatedAt) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (UserId is null ||
            Email is null ||
            DisplayName is null ||
            PhoneNumber is null ||
            PaymentValidationStatus is null ||
            MfaDecision is null ||
            ProjectPrimaryRegions is null ||
            ProjectBackupRegion is null ||
            Status is null ||
            NextStep is null ||
            Steps is null)
        {
            throw new JsonException("TerraScale onboarding state contains a null required field.");
        }

        foreach (var region in ProjectPrimaryRegions)
        {
            TerraScaleRegionCodes.ValidateResponse(region);
        }

        if (ProjectBackupRegion.Length > 0)
        {
            TerraScaleRegionCodes.ValidateResponse(ProjectBackupRegion);
        }
    }
}
