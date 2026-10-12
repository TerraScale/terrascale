namespace TerraScale.Dashboard.Platform.Persistence;

public static class DashboardStateSchema
{
    public const string Database = "dashboard";

    public static IReadOnlyList<string> Collections { get; } = Array.AsReadOnly(new[]
    {
        "sessions",
        "auth_credentials",
        "session_indexes",
        "login_throttle",
        "device_grants",
        "auth-flows",
        "auth-mail-budgets",
        "dashboard_accounts",
        "dashboard_account_identities",
        "dashboard_organizations",
        "dashboard_access_tokens",
        "dashboard_access_token_hashes",
        "dashboard_invitation_tokens",
        "dashboard_saved_queries",
        "dashboard_data_credentials",
        "dashboard_billing",
        "dashboard_internal_billing",
        "dashboard_stripe_customers",
        "dashboard_billing_changes",
        "dashboard_stripe_checkouts"
    });
}
