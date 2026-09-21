namespace TerraScale.Dashboard.Features.Onboarding.ViewModels;

/// <summary>One account activation step. Organization creation is automatic;
/// the visible flow covers account information, optional payment, and project setup.</summary>
public sealed record OnboardingWizardStep(string Key, string State);
