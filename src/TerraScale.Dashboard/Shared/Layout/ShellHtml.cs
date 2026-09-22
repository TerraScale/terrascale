using System.Text;
using Microsoft.AspNetCore.Html;

namespace TerraScale.Dashboard.Shared.Layout;

/// <summary>
/// Shell chrome renderer: the document skeleton, sidebar, project switcher,
/// account menu, and data sub-nav, straight from C# so pages never deal with
/// layout plumbing. A page slice opens with <c>@@Html.Raw(ShellHtml.Top(Model.Shell))</c>
/// and closes with <c>@@Html.Raw(ShellHtml.Bottom(Model.Shell))</c>. Markup
/// lives here only for chrome; page content stays in the .cshtml slices.
/// </summary>
public static class ShellHtml
{
    public static HtmlString Top(ShellModel shell)
    {
        var hasSubNav = shell.Route?.Id is RouteId.Data or RouteId.Authentication;
        var dataSubNav = hasSubNav ? " data-datasubnav" : "";
        var builder = new StringBuilder("""
            <!DOCTYPE html>
            <html lang="en" data-theme="dark">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
            """);
        builder.AppendLine(shell.PageTitle is { } title
            ? $"    <title>{Escape(title)} · TerraScale</title>"
            : "    <title>TerraScale</title>");
        builder.Append(DocumentHtml.Assets());
        builder.AppendLine("</head><body>");
        builder.AppendLine("    <div class=\"ts-shell\"" + dataSubNav + " data-signals=\"{navOpen:false}\" data-class:nav-open=\"$navOpen\">");
        builder.AppendLine("""        <div class="ts-nav-backdrop" data-on:click="$navOpen=false"></div>""");
        builder.Append(Sidebar(shell));
        if (shell.Route?.Id == RouteId.Data)
        {
            builder.Append(DataSubNav(shell));
        }
        else if (shell.Route?.Id == RouteId.Authentication)
        {
            builder.Append(AuthenticationSubNav(shell));
        }

        builder.AppendLine("        <div class=\"ts-inset\">");
        builder.AppendLine("""            <button type="button" class="ts-fab ts-fab-nav" aria-label="Open navigation" data-on:click="$navOpen=!$navOpen" data-attr:aria-expanded="$navOpen.toString()" aria-controls="ts-navigation" aria-expanded="false">""" + Icons.PanelLeft(18).Value + "</button>");
        builder.AppendLine("            <main class=\"ts-main\">");
        if (shell.PageTitle is { } pageTitle)
        {
            // Pages no longer show a visible title (DSH-125): the active nav
            // item already names the page. The heading stays as an sr-only h1
            // so the document outline and the <title> remain meaningful — the
            // same pattern as the account-creation header.
            builder.AppendLine($"                <h1 class=\"ts-sr-only\">{Escape(pageTitle)}</h1>");
        }
        return new HtmlString(builder.ToString());
    }

    public static HtmlString Bottom(ShellModel shell)
    {
        var builder = new StringBuilder("            </main>\n");
        if (shell.Route is { HasCreate: true } route)
        {
            var createHref = route.CreateAction switch
            {
                "database" => "/databases?create=1",
                "query" => "/data?create=1",
                "api-key" => "/api-keys?create=1",
                "invitation" => "/access?create=1",
                "project" => "/organization?create=1",
                "auth-user" => "/auth/users?create=1",
                _ => "#",
            };
            builder.AppendLine($"""                <a class="ts-fab ts-fab-create" href="{createHref}" aria-label="{Escape(route.PrimaryAction)}">{Icons.Plus(18).Value}<span class="ts-fab-label">Create</span></a>""");
        }

        builder.AppendLine("        </div>");
        builder.AppendLine("    </div>");
        if (shell.AllowPrefetch) builder.Append(NavigationPolicy.Rules);
        builder.AppendLine($"""    <script src="{AssetUrls.Url("/js/terrascale.js")}" defer></script>""");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return new HtmlString(builder.ToString());
    }

    private static string Sidebar(ShellModel shell)
    {
        var counts = shell.Store.Databases.GroupBy(database => database.ProjectId)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var builder = new StringBuilder("        <aside id=\"ts-navigation\" class=\"ts-sidebar\">\n");
        builder.AppendLine("            <div class=\"ts-sidebar-header\">");
        builder.Append(ProjectControls(shell, counts));
        builder.AppendLine("            </div>");
        builder.AppendLine("""            <div class="ts-sidebar-content">""");
        builder.AppendLine("                <ul class=\"ts-nav\">");
        foreach (var route in NavRoute.Main)
        {
            builder.Append(NavItem(shell, route, counts));
        }

        builder.AppendLine("                </ul>");
        if (shell.Store.OwnerSession)
        {
            builder.AppendLine("""                <hr class="ts-separator" />""");
            builder.AppendLine("""                <div class="ts-sidebar-group-label">Owner</div>""");
            builder.AppendLine("                <ul class=\"ts-nav\">");
            foreach (var route in NavRoute.Admin)
            {
                builder.Append(NavItem(shell, route, counts));
            }

            builder.AppendLine("                </ul>");
        }

        builder.AppendLine("            </div>");
        builder.AppendLine("            <div class=\"ts-sidebar-footer\">");
        builder.AppendLine($"""                <a class="ts-sidebar-brand" href="/" aria-label="TerraScale Dashboard"><img src="{AssetUrls.Url("/brand/logo-terrascale-blue.svg")}" alt="" aria-hidden="true" decoding="async" /></a>""");
        builder.Append(AccountMenu(shell));
        builder.AppendLine("            </div>");
        builder.AppendLine("        </aside>");
        return builder.ToString();
    }

    private static string NavItem(ShellModel shell, NavRoute route, IReadOnlyDictionary<string, int> counts)
    {
        var active = shell.ActiveHref == route.Href
            || (route.Id == RouteId.Authentication && shell.ActiveHref.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase));
        var icon = Icon(route.Icon);
        int? count = route.Id switch
        {
            RouteId.Databases => counts.GetValueOrDefault(shell.Store.ActiveProjectId),
            RouteId.Organization => shell.Store.Projects.Length,
            _ => null,
        };
        var builder = new StringBuilder($"                    <li class=\"ts-nav-item\">");
        var prefetch = shell.AllowPrefetch && !active && NavigationPolicy.IsPrefetchPath(route.Href) ? " data-prefetch" : "";
        builder.Append($"""<a class="ts-nav-link" href="{route.Href}" style="--ts-nav-accent:{route.Accent}"{prefetch}{(active ? " aria-current=\"page\"" : "")}>""");
        builder.Append("""<span class="ts-nav-indicator" aria-hidden="true"></span>""");
        builder.Append($"""<span class="ts-nav-icon">{icon}</span>""");
        builder.Append($"<span>{Escape(route.Label)}</span></a>");
        if (count is > 0)
        {
            var tone = active ? "ts-nav-count-active" : "ts-nav-count-neutral";
            builder.Append($"""<span class="ts-nav-count {tone}" aria-hidden="true">{(count > 99 ? "99+" : count.ToString())}</span>""");
        }

        builder.AppendLine("</li>");
        return builder.ToString();
    }

    private static string ProjectControls(ShellModel shell, IReadOnlyDictionary<string, int> counts)
    {
        // v1 ProjectSwitcher: one collapsed trigger; details live in the popover.
        var store = shell.Store;
        var project = store.ActiveProject;
        var databaseCount = counts.GetValueOrDefault(project.Id);
        var others = store.Projects.Where(p => p.Id != project.Id).ToArray();
        var builder = new StringBuilder("                <div class=\"ts-project-controls\" data-signals=\"{projectSearch:''}\">");
        builder.AppendLine("""                    <details class="ts-menu ts-project-menu">""");
        builder.AppendLine($"""                        <summary class="ts-btn ts-btn-secondary ts-btn-lg ts-project-trigger">{Icons.FolderKanban(16).Value}<span class="ts-project-trigger-label">{Escape(project.Name)}</span>{Icons.ChevronDown(15).Value}</summary>""");
        builder.AppendLine("                        <div class=\"ts-menu-panel ts-project-panel\">");
        builder.AppendLine("""                            <div class="ts-project-panel-header">TerraScale</div>""");
        builder.AppendLine("                            <div class=\"ts-project-panel-scroll\">");
        builder.AppendLine("""                                <div class="ts-project-section-label">Current project</div>""");
        builder.AppendLine("""                                <div class="ts-current-project">""");
        builder.AppendLine("""                                    <div class="ts-current-project-top">""");
        builder.AppendLine("""                                        <div class="ts-current-project-name"><span class="ts-status-dot" aria-hidden="true"></span><span class="ts-project-name">""" + Escape(project.Name) + "</span></div>");
        builder.AppendLine($"""                                        <div class="ts-project-actions"><span class="ts-active-label">Active</span><a class="ts-project-rename" href="/organization" aria-label="Rename project">{Icons.Pencil(13).Value}</a></div>""");
        builder.AppendLine("                                    </div>");
        builder.AppendLine("""                                    <div class="ts-project-meta">""");
        builder.AppendLine($"""                                        <span class="ts-project-meta-item">{Icons.MapPin(12).Value} {Escape(project.Region)}</span>""");
        builder.AppendLine($"""                                        <span class="ts-project-meta-item">{Icons.Database(12).Value} {databaseCount} {(databaseCount == 1 ? "database" : "databases")}</span>""");
        builder.AppendLine("                                    </div>");
        builder.AppendLine("                                </div>");
        if (others.Length > 0)
        {
            builder.AppendLine("""                                <div class="ts-project-search-section">""");
            builder.AppendLine($"""                                    <div class="ts-project-search">{Icons.Search(14).Value}<input class="ts-input" type="search" placeholder="Search projects" aria-label="Search projects" data-bind="projectSearch" /></div>""");
            builder.AppendLine("""                                    <div class="ts-project-section-label">Switch to</div>""");
            builder.AppendLine("""                                    <div class="ts-project-list">""");
            foreach (var option in others)
            {
                var optionDatabases = counts.GetValueOrDefault(option.Id);
                builder.AppendLine($"                                        <form method=\"post\" action=\"/api/v1/dashboard/switch-project\" data-show=\"$projectSearch === '' || {Escape(System.Text.Json.JsonSerializer.Serialize(option.Name.ToLowerInvariant()))}.includes($projectSearch.toLowerCase())\">");
                builder.AppendLine($"                                            <input type=\"hidden\" name=\"projectId\" value=\"{option.Id}\" />");
                builder.AppendLine($"                                            <input type=\"hidden\" name=\"returnUrl\" value=\"{shell.ActiveHref}\" />");
                builder.AppendLine("                                            <button type=\"submit\" class=\"ts-project-option\">");
                builder.AppendLine($"""                                                <span class="ts-project-option-icon">{Icons.FolderKanban(15).Value}</span>""");
                builder.AppendLine("""                                                <span class="ts-project-option-content">""");
                builder.AppendLine($"""                                                    <span class="ts-project-option-heading"><span class="ts-project-option-name">{Escape(option.Name)}</span></span>""");
                builder.AppendLine($"""                                                    <span class="ts-project-meta ts-project-meta-compact"><span class="ts-project-meta-item">{Icons.MapPin(12).Value} {Escape(option.Region)}</span><span class="ts-project-meta-item">{Icons.Database(12).Value} {optionDatabases}</span></span>""");
                builder.AppendLine("                                                </span>");
                builder.AppendLine("                                            </button>");
                builder.AppendLine("                                        </form>");
            }

            builder.AppendLine("                                    </div>");
            builder.AppendLine("                                </div>");
        }

        builder.AppendLine("                            </div>");
        builder.AppendLine("""                            <div class="ts-project-footer">""");
        builder.AppendLine($"""                                <a class="ts-project-footer-link" href="/organization?create=1">{Icons.Plus(15).Value} Create new project</a>""");
        builder.AppendLine($"""                                <a class="ts-project-footer-link" href="/settings">{Icons.Settings(15).Value} Project settings</a>""");
        builder.AppendLine($"""                                <a class="ts-project-footer-link" href="/organization">{Icons.Building2(15).Value} Browse all workspaces</a>""");
        builder.AppendLine("                            </div>");
        builder.AppendLine("                        </div>");
        builder.AppendLine("                    </details>");
        builder.AppendLine("                </div>");
        return builder.ToString();
    }

    private static string AccountMenu(ShellModel shell)
    {
        var unread = shell.UnreadNotifications;
        var unreadDot = unread > 0 ? $"""<span class="ts-account-unread-dot" role="status" aria-label="{unread} unread notifications"></span>""" : "";
        var unreadBadge = unread > 0 ? $"""<span class="ts-menu-badge">{(unread > 9 ? "9+" : unread.ToString())}</span>""" : "";
        return $"""
                    <details class="ts-menu ts-menu-end ts-menu-up ts-account">
                        <summary class="ts-account-trigger" aria-label="Account menu">
                            <span class="ts-account-avatar" aria-hidden="true">{Escape(shell.AccountInitials)}</span>
                            <span class="ts-account-meta">
                                <span class="ts-account-name">{Escape(shell.AccountName)}</span>
                                <span class="ts-account-email">{Escape(shell.AccountEmail)}</span>
                            </span>
                            {unreadDot}
                            <span class="ts-account-chevron">{Icons.ChevronUp(16).Value}</span>
                        </summary>
                        <div class="ts-menu-panel">
                            <a class="ts-menu-item" href="/notifications"><span class="ts-menu-item-icon">{Icons.Bell(16).Value}</span><span class="ts-menu-label">Notifications</span>{unreadBadge}</a>
                            <a class="ts-menu-item" href="/settings"><span class="ts-menu-item-icon">{Icons.Settings(16).Value}</span><span class="ts-menu-label">Settings</span></a>
                            <hr class="ts-menu-separator" />
                            <form method="post" action="/api/v1/auth/logout">
                                <button type="submit" class="ts-menu-item"><span class="ts-menu-item-icon">{Icons.LogOut(16).Value}</span><span class="ts-menu-label">Sign out</span></button>
                            </form>
                        </div>
                    </details>
            """;
    }

    private static string DataSubNav(ShellModel shell)
    {
        var view = shell.ActiveHref.StartsWith("/data", StringComparison.OrdinalIgnoreCase);
        if (!view)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("""        <aside class="ts-datasubnav" aria-label="Data views">""");
        var viewKey = shell.ActiveHref.Contains("view=", StringComparison.Ordinal)
            ? shell.ActiveHref.Split("view=", StringSplitOptions.RemoveEmptyEntries)[^1]
            : "";
        builder.AppendLine("""            <div class="ts-datasubnav-header">TABLES</div>""");
        builder.AppendLine("""            <div class="ts-datasubnav-menu">""");
        builder.AppendLine(SubNavLink("/data", "Saved queries", "BookOpen", viewKey.Length == 0));
        builder.AppendLine(SubNavLink("/data?view=table", "Collection documents", "Table", viewKey == "table"));
        builder.AppendLine(SubNavLink("/data?view=editor", "Document editor", "FileText", viewKey == "editor"));
        builder.AppendLine(SubNavLink("/data?view=sql", "SQL console", "Terminal", viewKey == "sql"));
        builder.AppendLine("            </div>");
        builder.AppendLine("        </aside>");
        return builder.ToString();
    }

    private static string AuthenticationSubNav(ShellModel shell)
    {
        var builder = new StringBuilder("        <aside class=\"ts-datasubnav\" aria-label=\"Authentication views\">");
        builder.AppendLine("            <div class=\"ts-datasubnav-header\">AUTHENTICATION</div>");
        builder.AppendLine("            <div class=\"ts-datasubnav-menu\">");
        builder.AppendLine(SubNavLink("/auth", "Overview", "ShieldCheck", shell.ActiveHref == "/auth"));
        builder.AppendLine(SubNavLink("/auth/users", "Users", "Users", shell.ActiveHref == "/auth/users"));
        builder.AppendLine(SubNavLink("/auth/sessions", "Sessions", "Monitor", shell.ActiveHref == "/auth/sessions"));
        builder.AppendLine(SubNavLink("/auth/organizations", "Organizations", "Building2", shell.ActiveHref == "/auth/organizations"));
        builder.AppendLine(SubNavLink("/auth/memberships", "Memberships", "Users", shell.ActiveHref == "/auth/memberships"));
        builder.AppendLine(SubNavLink("/auth/invitations", "Invitations", "MailPlus", shell.ActiveHref == "/auth/invitations"));
        builder.AppendLine(SubNavLink("/auth/domains", "Domains", "Globe", shell.ActiveHref == "/auth/domains"));
        builder.AppendLine(SubNavLink("/auth/connections", "Connections", "GlobeLock", shell.ActiveHref == "/auth/connections"));
        builder.AppendLine(SubNavLink("/auth/directories", "Directories", "FolderOpen", shell.ActiveHref == "/auth/directories"));
        builder.AppendLine(SubNavLink("/auth/clients", "OAuth clients", "KeySquare", shell.ActiveHref == "/auth/clients"));
        builder.AppendLine(SubNavLink("/auth/templates", "JWT templates", "FileJson", shell.ActiveHref == "/auth/templates"));
        builder.AppendLine(SubNavLink("/auth/keys", "Management keys", "KeyRound", shell.ActiveHref == "/auth/keys"));
        builder.AppendLine(SubNavLink("/auth/webhooks", "Webhooks", "Webhook", shell.ActiveHref == "/auth/webhooks"));
        builder.AppendLine(SubNavLink("/auth/deliveries", "Deliveries", "Send", shell.ActiveHref == "/auth/deliveries"));
        builder.AppendLine(SubNavLink("/auth/audit", "Audit log", "ScrollText", shell.ActiveHref == "/auth/audit"));
        builder.AppendLine(SubNavLink("/auth/billing", "Billing", "CreditCard", shell.ActiveHref == "/auth/billing"));
        builder.AppendLine(SubNavLink("/auth/quickstart", "Quickstart", "Terminal", shell.ActiveHref == "/auth/quickstart"));
        builder.AppendLine(SubNavLink("/auth/settings", "Settings", "Settings", shell.ActiveHref == "/auth/settings"));
        builder.AppendLine("            </div>");
        builder.AppendLine("        </aside>");
        return builder.ToString();
    }

    private static string SubNavLink(string href, string label, string icon, bool active)
    {
        var current = active ? " aria-current=\"page\"" : "";
        return $"""                <a class="ts-nav-link" href="{href}" style="--ts-nav-accent:#14B8A6"{current}><span class="ts-nav-indicator" aria-hidden="true"></span><span class="ts-nav-icon">{Icon(icon, 16)}</span><span>{label}</span></a>""";
    }

    private static string Icon(string name, int size = 18) => name switch
    {
        "Home" => Icons.Home(size).Value,
        "Database" => Icons.Database(size).Value,
        "Table" => Icons.Table(size).Value,
        "KeyRound" => Icons.KeyRound(size).Value,
        "Users" => Icons.Users(size).Value,
        "Gauge" => Icons.Gauge(size).Value,
        "ScrollText" => Icons.ScrollText(size).Value,
        "Bell" => Icons.Bell(size).Value,
        "Settings" => Icons.Settings(size).Value,
        "UserRound" => Icons.UserRound(size).Value,
        "Building" => Icons.Building(size).Value,
        "Network" => Icons.Network(size).Value,
        "Fingerprint" => Icons.Fingerprint(size).Value,
        "Building2" => Icons.Building2(size).Value,
        "Activity" => Icons.Activity(size).Value,
        "LockKeyhole" => Icons.LockKeyhole(size).Value,
        "BookOpen" => Icons.BookOpen(size).Value,
        "FileText" => Icons.FileText(size).Value,
        "Terminal" => Icons.Terminal(size).Value,
        _ => Icons.Circle(size).Value,
    } ?? "";

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
