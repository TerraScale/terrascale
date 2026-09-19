namespace TerraScale.Dashboard2;

/// <summary>One wizard step state: account-information, organization,
/// payment-method, payment-validation, project.</summary>
public sealed record OnboardingWizardStep(string Key, string State);
