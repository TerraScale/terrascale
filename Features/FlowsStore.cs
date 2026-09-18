using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

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

    public AgentApprovalRequest DemoAgentApproval { get; } = new(
        AgentName: "deploy-copilot",
        ExpiresAt: "2026-09-18T15:45:00Z",
        BindingMessage: "Run TerraScale CLI from CI job #4821",
        Capabilities: ["databases:list", "documents:read", "documents:write"],
        Reasons: new Dictionary<string, string>
        {
            ["databases:list"] = "Enumerate databases in the active project.",
            ["documents:read"] = "Read deployment manifests stored as documents.",
            ["documents:write"] = "Write back deployment status records.",
        },
        ConstraintsJson: """
            {
              "maxWriteBytesPerHour": 10485760,
              "allowedCollections": ["deployments", "status"],
              "regions": ["us-east-1", "eu-central-1"]
            }
            """);

    public sealed record OAuthConsentClient(string ClientId, string ClientName);

    public OAuthConsentClient DemoOAuthClient { get; } = new("app_01JCLIENT7Q9W2E4R6T", "Northwind Deploy");

    public string[] DemoOAuthScopes { get; } = ["openid", "offline_access", "databases:read", "documents:write"];

    // --- Onboarding wizard state (demo session) ---------------------------

    public sealed record OnboardingStep(string Key, string State);

    private ImmutableArray<OnboardingStep> _onboardingSteps =
    [
        new("account-information", "completed"),
        new("organization", "completed"),
        new("payment-method", "current"),
        new("payment-validation", "pending"),
        new("project", "pending"),
    ];

    public ImmutableArray<OnboardingStep> OnboardingSteps => _onboardingSteps;

    public string OnboardingNextStep =>
        _onboardingSteps.FirstOrDefault(s => s.State is "current" or "pending")?.Key is { Length: > 0 } next
            ? next
            : "project";

    public void CompleteOnboardingStep(string key)
    {
        _onboardingSteps = [.. _onboardingSteps.Select(s => s.Key == key ? s with { State = "completed" } : s)];
        var advanced = false;
        _onboardingSteps = [.. _onboardingSteps.Select(s =>
        {
            if (!advanced && s.State == "pending")
            {
                advanced = true;
                return s with { State = "current" };
            }

            return s;
        })];
    }

    public void RespondToInvitation(string invitationId, string status) =>
        Invitations = [.. Invitations.Select(i => i.Id == invitationId ? i with { Status = status } : i)];
}
