using System.Collections.Immutable;

namespace TerraScale.Dashboard;

/// <summary>Seeded state for the secondary flows: onboarding wizard,
/// CLI device review, agent approval, and OAuth consent demo client.</summary>
public sealed partial class DashboardStore
{
    public string DemoCliUserCode => "TSCL-7F3K-9Q2D";

    public (string[] Scopes, string[] Resources) DemoCliReview => (
        ["databases:read", "documents:write", "usage:read"],
        ["organizations", "databases"]);

    public static string CliScopeDescription(string scope) => scope switch
    {
        "databases:read" => "List databases and read metadata.",
        "documents:write" => "Create and update documents.",
        "usage:read" => "Read current-cycle usage summaries.",
        _ => "Additional access as granted by your roles.",
    };

    public sealed record AgentApprovalRequest(
        string AgentName,
        string ExpiresAt,
        string? BindingMessage,
        string[] Capabilities,
        Dictionary<string, string> Reasons,
        string? ConstraintsJson);

    /// <summary>Baseline request matching the ts-dashboard agent approval page:
    /// device-authorization flow with no pre-declared capabilities (the page
    /// shows the validation note instead of capability chips).</summary>
    public AgentApprovalRequest DemoAgentApproval { get; } = new(
        AgentName: "Agent requesting access",
        ExpiresAt: "2026-09-18T17:40:00Z",
        BindingMessage: "This agent is requesting access to your TerraScale Dashboard.",
        Capabilities: [],
        Reasons: new Dictionary<string, string>(),
        ConstraintsJson: null);

    public sealed record OAuthConsentClient(string ClientId, string ClientName);

    public OAuthConsentClient DemoOAuthClient { get; } = new("app_01JCLIENT7Q9W2E4R6T", "Northwind Deploy");

    public string[] DemoOAuthScopes { get; } = ["openid", "offline_access", "databases:read", "documents:write"];

    // --- Onboarding wizard state (per account world) ------------------------

    public ImmutableArray<OnboardingWizardStep> OnboardingSteps => World.OnboardingStepsValue;

    public string OnboardingNextStep =>
        World.OnboardingStepsValue.FirstOrDefault(s => s.State is "current" or "pending")?.Key is { Length: > 0 } next
            ? next
            : "project";

    public void CompleteOnboardingStep(string key)
    {
        World.OnboardingStepsValue = [.. World.OnboardingStepsValue.Select(s => s.Key == key ? s with { State = "completed" } : s)];
        var advanced = false;
        World.OnboardingStepsValue = [.. World.OnboardingStepsValue.Select(s =>
        {
            if (!advanced && s.State == "pending")
            {
                advanced = true;
                return s with { State = "current" };
            }

            return s;
        })];
        if (key == "project")
        {
            World.OnboardingCompleted = true;
        }
    }

    public void RespondToInvitation(string invitationId, string status) =>
        Invitations = [.. Invitations.Select(i => i.Id == invitationId ? i with { Status = status } : i)];
}
