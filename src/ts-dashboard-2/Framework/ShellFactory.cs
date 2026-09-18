namespace TerraScale.Dashboard2;

public static class ShellFactory
{
    public static ShellModel Create(DashboardStore store, string href)
    {
        var user = store.CurrentUser;
        var route = NavRoute.Find(href);
        return new ShellModel(
            ActiveHref: href,
            Route: route,
            Store: store,
            AccountName: user.Name.Length > 0 ? user.Name : user.Email,
            AccountEmail: user.Email,
            AccountInitials: store.Initials(user.Name, user.Email),
            UnreadNotifications: store.UnreadNotifications,
            PageTitle: route?.Label ?? "TerraScale");
    }
}
