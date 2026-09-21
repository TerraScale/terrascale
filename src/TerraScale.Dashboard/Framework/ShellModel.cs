namespace TerraScale.Dashboard;

public sealed record ShellModel(
    string ActiveHref,
    NavRoute? Route,
    DashboardStore Store,
    string AccountName,
    string AccountEmail,
    string AccountInitials,
    int UnreadNotifications,
    string? PageTitle);
