using System.Collections.Immutable;

namespace TerraScale.Dashboard.Features.Authentication;

/// <summary>Demo state is separate from dashboard accounts and scoped to the selected project.</summary>
public sealed class AuthInstanceState
{
    public ImmutableArray<AuthResource> Resources { get; set; } = [];
    public string Plan { get; set; } = "Free";
    public string? PendingSecret { get; set; }
    public string Name { get; set; } = "My application";
    public string LogoUrl { get; set; } = "";
    public string ThemeColor { get; set; } = "#3B82F6";

    public static AuthInstanceState Create(string name, bool seeded)
    {
        var state = new AuthInstanceState { Name = name };
        if (!seeded) return state;
        var now = DateTimeOffset.UtcNow;
        void Add(string id, string section, string status, params (string, string)[] values) =>
            state.Resources = state.Resources.Add(new(id, section, values.ToImmutableDictionary(x => x.Item1, x => x.Item2), status, now.AddDays(-2)));
        Add("usr_demo_alex", "users", "Active", ("name", "Alex Morgan"), ("email", "alex@example.com"), ("username", "alex"), ("public_metadata", "{\"plan\":\"scale\"}"), ("private_metadata", "{}"));
        Add("usr_demo_jules", "users", "Active", ("name", "Jules Chen"), ("email", "jules@example.com"), ("username", "jules"), ("public_metadata", "{}"), ("private_metadata", "{}"));
        Add("org_demo_acme", "organizations", "Active", ("name", "Acme Studio"), ("slug", "acme-studio"));
        Add("mem_demo_alex", "memberships", "Active", ("name", "Alex · Acme"), ("organization", "org_demo_acme"), ("user", "usr_demo_alex"), ("role", "admin"));
        Add("ses_demo_browser", "sessions", "Active", ("name", "Alex's browser"), ("user", "usr_demo_alex"), ("device", "Chrome on Linux"), ("ip", "203.0.113.24"));
        Add("ses_demo_mobile", "sessions", "Active", ("name", "Jules's phone"), ("user", "usr_demo_jules"), ("device", "Safari on iOS"), ("ip", "198.51.100.7"));
        Add("clt_demo_web", "clients", "Active", ("name", "Acme web app"), ("type", "Public client"), ("redirect_uris", "http://localhost:3000/callback"), ("allowed_origins", "http://localhost:3000"));
        Add("jtp_demo_api", "templates", "Active", ("name", "application-api"), ("claims", "{\"email\":\"{{user.email}}\"}"), ("lifetime_seconds", "300"));
        Add("wh_demo_sync", "webhooks", "Configured", ("url", "https://example.com/hooks/auth"), ("events", "user.created,user.updated"));
        Add("del_demo_retry", "deliveries", "Failed", ("name", "User profile sync"), ("endpoint", "wh_demo_sync"), ("event", "user.updated"), ("attempts", "3"));
        Add("evt_demo_setup", "audit", "Recorded", ("name", "instance.created"), ("actor", "Demo setup"), ("target", name));
        return state;
    }
}
