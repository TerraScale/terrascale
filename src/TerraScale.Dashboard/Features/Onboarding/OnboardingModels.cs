using System.Globalization;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Onboarding;

public interface IOnboardingProjectProvisioner
{
    // Complete the existing provider-backed Personal project; do not invent provider IDs.
    Task<ProjectRecord> CompleteProjectAsync(AccountContext context, string name, string region, CancellationToken cancellationToken);
}

public sealed record OnboardingStep(string Key, string Label, string Number, string State, bool Completed, bool Current);

public static class OnboardingProgress
{
    public static IReadOnlyList<OnboardingStep> Steps(string step)
    {
        var definitions = new[] { ("account-information", "Account"), ("payment-method", "Payment"), ("project", "Project") };
        var current = Array.FindIndex(definitions, definition => definition.Item1 == step);
        return definitions.Select((definition, index) => new OnboardingStep(definition.Item1, definition.Item2, (index + 1).ToString(CultureInfo.InvariantCulture),
            current < 0 || index < current ? "COMPLETED" : index == current ? "CURRENT" : "NOT STARTED", current < 0 || index < current, index == current)).ToArray();
    }
}

public sealed record OnboardingPage(DocumentModel Document, string CsrfToken,
    string DisplayName = "", string Email = "", string Error = "",
    bool PaymentCanceled = false, string Plan = "", string Step = "account-information", IReadOnlyList<BillingPlan>? Plans = null,
    IReadOnlyList<string>? Regions = null, string ProjectName = "Personal", string Region = "")
{
    public IReadOnlyList<OnboardingStep> Steps => OnboardingProgress.Steps(Step);
    public int CurrentStep => Steps.Select((step, index) => (step, index)).FirstOrDefault(item => item.step.Current).index + 1;
    public int Progress => Steps.Count(step => step.Completed) * 100 / Steps.Count;
}
