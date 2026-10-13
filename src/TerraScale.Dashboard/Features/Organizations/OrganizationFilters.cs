using TerraScale.Dashboard.Features.Accounts;

namespace TerraScale.Dashboard.Features.Organizations;

public sealed record OrganizationFilters(string Query = "", string Role = "all", int Page = 1, string Search = "", string Action = "all", string Severity = "all", string Range = "7d", DateTimeOffset? Now = null)
{
    public static OrganizationFilters For(HttpContext http) => new(
        http.Request.Query["q"].ToString().Trim(), http.Request.Query["role"].LastOrDefault() is { Length: > 0 } role ? role : "all",
        int.TryParse(http.Request.Query["page"], out var page) ? Math.Max(1, page) : 1,
        http.Request.Query["search"].ToString().Trim(), http.Request.Query["action"].ToString() is { Length: > 0 } action ? action : "all",
        http.Request.Query["severity"].ToString() is "info" or "warn" or "critical" ? http.Request.Query["severity"].ToString() : "all",
        http.Request.Query["range"].ToString() is "24h" or "30d" or "90d" ? http.Request.Query["range"].ToString() : "7d",
        http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());

    public IReadOnlyList<AuditRecord> ApplyAudit(IEnumerable<AuditRecord> events, int? retentionDays = null)
    {
        var days = Range switch { "24h" => 1, "30d" => 30, "90d" => 90, _ => 7 };
        if (retentionDays is >= 1 and <= 90) days = Math.Min(days, retentionDays.Value);
        var from = (Now ?? DateTimeOffset.UtcNow).AddDays(-days);
        return events.Where(item => (item.OccurredAt == DateTimeOffset.MinValue || item.OccurredAt >= from) && (Action == "all" || item.Action == Action) &&
            (Severity == "all" || AuditSeverity(item) == Severity) &&
            (Search.Length == 0 || string.Join(' ', item.Actor, item.Action, item.Target, item.TargetType, item.Source, AuditSeverity(item), item.OccurredAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")).Contains(Search, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.OccurredAt).ThenBy(item => item.Id).ToArray();
    }
    public static string AuditSeverity(AuditRecord item) => item.Risk?.ToLowerInvariant() switch
    {
        "high" or "critical" => "critical",
        "medium" or "warn" or "warning" => "warn",
        _ => "info"
    };
    // These presentation classifiers preserve the original Go rules over the
    // event's reported fields; they do not grant permissions or create events.
    public static string AuditActorType(AuditRecord item)
    {
        var actor = item.Actor.ToLowerInvariant();
        return actor.Length == 0 || actor == "system" || actor.Contains("service") || actor.Contains("bot") || actor.Contains("m2m") ? "service" : "user";
    }
    public static string AuditOutcome(AuditRecord item)
    {
        var text = string.Join(' ', item.Action, item.Target, item.Risk).ToLowerInvariant();
        return text.Contains("fail") || text.Contains("denied") ? "failure" : "success";
    }
    public static string AuditActionLabel(AuditRecord item) => item.Action switch
    {
        "api_key.created" => "API key created",
        "member.role_changed" => "Member role changed",
        "document.batch_upsert" => "Documents batch upserted",
        "database.deleted" => "Database deleted",
        "backup.restore_completed" => "Backup restore completed",
        _ => Capitalize(string.Join(' ', item.Action.Replace('.', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)))
    };
    private static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
    public string AuditUrl(int page = 1, string? format = null) => (format is null ? "/organization?tab=audit" : "/audit/export?format=" + format) +
        "&range=" + Uri.EscapeDataString(Range) + "&severity=" + Uri.EscapeDataString(Severity) + "&action=" + Uri.EscapeDataString(Action) + "&search=" + Uri.EscapeDataString(Search) + (page > 1 ? "&page=" + page : "");
}

public sealed record InvitationRow(string Id, string Email, string Role, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool? Pending = null)
{
    public bool Open => Pending ?? Status == "pending";
}

public sealed partial record OrganizationPage
{
    public bool CanManage => Shell.Context.Role == "owner" && Shell.Context.Organization?.OwnerUserId == Shell.Context.UserId;
    public bool CanProvision => CanManage && Shell.Context.Organization?.CanProvision(Shell.Context.UserId) == true;
    public OrganizationFilters Filter => Filters ?? new();
    public IReadOnlyList<MemberRecord> MatchingMembers => (Access?.Members ?? []).Where(member =>
        (Filter.Role == "all" || member.Role == Filter.Role) &&
        (Filter.Query.Length == 0 || string.Join(' ', member.Name, member.Email, member.UserId).Contains(Filter.Query, StringComparison.OrdinalIgnoreCase))).OrderBy(member => member.Name).ThenBy(member => member.UserId).ToArray();
    public int MemberPages => Math.Max(1, (MatchingMembers.Count + 9) / 10);
    public int MemberPage => Math.Clamp(Filter.Page, 1, MemberPages);
    public int MemberTotal => Access?.Members.Count ?? 0;
    public IReadOnlyList<MemberRecord> MemberRows => MatchingMembers.Skip((MemberPage - 1) * 10).Take(10).ToArray();
    public string MemberUrl(int page) => "/organization?tab=members&q=" + Uri.EscapeDataString(Filter.Query) + "&role=" + Uri.EscapeDataString(Filter.Role) + (page > 1 ? "&page=" + page : "");
    public IReadOnlyList<InvitationRow> InvitationRows
    {
        get
        {
            var now = Filter.Now ?? DateTimeOffset.UtcNow;
            var rows = (Access?.Invitations ?? []).Select(invite => new InvitationRow(invite.Id, invite.Email, invite.Role,
                invite.Status == "pending" && invite.ExpiresAt != DateTimeOffset.MinValue && invite.ExpiresAt <= now ? "expired" : invite.Status,
                invite.CreatedAt, invite.ExpiresAt, invite.Status == "pending")).ToDictionary(invite => invite.Id);
            return rows.Values.OrderBy(invite => !invite.Open).ThenBy(invite => invite.Email, StringComparer.Ordinal).ToArray();
        }
    }
    public int InvitationPages => Math.Max(1, (InvitationRows.Count + 9) / 10);
    public int InvitationPage => Math.Clamp(Filter.Page, 1, InvitationPages);
    public IReadOnlyList<InvitationRow> InvitationPageRows => InvitationRows.Skip((InvitationPage - 1) * 10).Take(10).ToArray();
    public string InvitationUrl(int page) => "/organization?tab=invitations" + (page > 1 ? "&page=" + page : "");
    public static string RoleLabel(string role) => role.Length == 0 ? "Unknown" : char.ToUpperInvariant(role[0]) + role[1..];
    public static string RoleFilterLabel(string role) => role switch { "all" => "All roles", "owner" => "Organization owner", _ => RoleLabel(role) };
    public static string RoleTone(string role) => role switch { "owner" => "violet", "admin" => "teal", "member" => "blue", _ => "slate" };
    public static string InvitationTone(string status) => status switch { "pending" => "amber", "accepted" => "emerald", "declined" => "rose", _ => "slate" };
    public IReadOnlyList<AuditRecord> AuditMatches => Filter.ApplyAudit(Access?.AuditEvents ?? [], Access?.AuditRetentionDays);
    public int AuditPages => Math.Max(1, (AuditMatches.Count + 9) / 10);
    public int AuditPage => Math.Clamp(Filter.Page, 1, AuditPages);
    public IReadOnlyList<AuditRecord> AuditRows => AuditMatches.Skip((AuditPage - 1) * 10).Take(10).ToArray();
}

public sealed record OrganizationMemberModel(OrganizationPage Page, MemberRecord Member)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Member.Name) ? Member.Email : Member.Name.Trim();
    public string Initials => string.Concat(DisplayName.Split([' ', '.', '@', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Take(2).Select(part => part.EnumerateRunes().First().ToString().ToUpperInvariant()));
    public bool CanEdit => Page.CanManage && Member.UserId != Page.Shell.Context.UserId && Member.Role != "owner";
}
