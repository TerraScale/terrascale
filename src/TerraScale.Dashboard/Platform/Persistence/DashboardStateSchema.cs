namespace TerraScale.Dashboard.Platform.Persistence;

// TerraBase seeds this database under the ts-dashboard project at startup.
// ts-auth owns the matching TerraScale organization, owner and project identities.
public static class DashboardStateSchema
{
    public const string ProjectId = "00000000000000000000000005";
    public const string Database = "ts-dashboard";

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
        "dashboard_saved_queries",
        "dashboard_billing",
        "dashboard_internal_billing",
        "dashboard_stripe_customers",
        "dashboard_billing_changes",
        "dashboard_stripe_checkouts"
    });
}
