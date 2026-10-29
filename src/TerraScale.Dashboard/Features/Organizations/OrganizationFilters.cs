
namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed record OrganizationFilters(string Query = "", string Role = "all", int Page = 1, string Search = "", string Action = "all", string Severity = "all", string Range = "7d", DateTimeOffset? Now = null)
    {
        public static OrganizationFilters For(HttpContext http)
        {
            return new(
            http.Request.Query["q"].ToString().Trim(), http.Request.Query["role"].LastOrDefault() is { Length: > 0 } role ? role : "all",
            int.TryParse(http.Request.Query["page"], out var page) ? Math.Max(1, page) : 1,
            http.Request.Query["search"].ToString().Trim(), http.Request.Query["action"].ToString() is { Length: > 0 } action ? action : "all",
            http.Request.Query["severity"].ToString() is "info" or "warn" or "critical" or "unknown" ? http.Request.Query["severity"].ToString() : "all",
            http.Request.Query["range"].ToString() is "24h" or "30d" or "90d" or "365d" ? http.Request.Query["range"].ToString() : "7d",
            http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
        }

        public static IReadOnlyList<(string Id, string Label, int Days)> Ranges { get; } =
            [("24h", "Last 24 hours", 1), ("7d", "Last 7 days", 7), ("30d", "Last 30 days", 30), ("90d", "Last 90 days", 90), ("365d", "Last 365 days", 365)];
        public IReadOnlyList<AuditRecord> ApplyAudit(IEnumerable<AuditRecord> events, int? retentionDays = null)
        {
            var days = Ranges.FirstOrDefault(range => range.Id == Range).Days;
            if (days == 0)
            {
                days = 7;
            }

            if (retentionDays is >= 1)
            {
                days = Math.Min(days, retentionDays.Value);
            }

            var from = (Now ?? DateTimeOffset.UtcNow).AddDays(-days);
            return [.. events.Where(item => (item.OccurredAt == DateTimeOffset.MinValue || item.OccurredAt >= from) && (Action == "all" || item.Action == Action) &&
                (Severity == "all" || AuditSeverity(item) == Severity) &&
                (Search.Length == 0 || string.Join(' ', item.Actor, item.Action, item.Target, item.TargetType, item.Source, AuditSeverity(item), item.OccurredAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")).Contains(Search, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(item => item.OccurredAt).ThenBy(item => item.Id)];
        }
        public static string AuditSeverity(AuditRecord item)
        {
            return item.Risk?.ToLowerInvariant() switch
            {
                "high" or "critical" => "critical",
                "medium" or "warn" or "warning" => "warn",
                "low" or "info" or "informational" => "info",
                _ => "unknown"
            };
        }

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
        public static string AuditActionLabel(AuditRecord item)
        {
            return item.Action switch
            {
                "session.created" => "Signed in",
                "api_key.created" => "API key created",
                "member.role_changed" => "Member role changed",
                "document.batch_upsert" => "Documents batch upserted",
                "database.deleted" => "Database deleted",
                "backup.restore_completed" => "Backup restore completed",
                "organization.mfa_required" => "Two-factor required for members",
                "organization.mfa_optional" => "Two-factor no longer required",
                "member.mfa_enabled" => "Member turned on two-factor",
                "member.mfa_disabled" => "Member turned off two-factor",
                "member.mfa_recovery_code_used" => "Member used a recovery code",
                _ => Capitalize(string.Join(' ', item.Action.Replace('.', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            };
        }

        private static string Capitalize(string value)
        {
            return value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
        }

        public string AuditUrl(int page = 1, string? format = null)
        {
            return (format is null ? "/organization?tab=audit" : "/audit/export?format=" + format) +
                "&range=" + Uri.EscapeDataString(Range) + "&severity=" + Uri.EscapeDataString(Severity) + "&action=" + Uri.EscapeDataString(Action) + "&search=" + Uri.EscapeDataString(Search) + (page > 1 ? "&page=" + page : "");
        }
    }

    public sealed record InvitationRow(string Id, string Email, string Role, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool? Pending = null)
    {
        public bool Open => Pending ?? Status == "pending";
    }

    public sealed partial record OrganizationPage
    {
        public bool CanManage => Shell.Context.CanManage;
        public bool CanProvision => CanManage && Shell.Context.Organization?.CanProvision(Shell.Context.UserId) == true;
        public OrganizationFilters Filter => Filters ?? new();
        public IReadOnlyList<MemberRecord> MatchingMembers => [.. (Access?.Members ?? []).Where(member =>
            (Filter.Role == "all" || member.Role == Filter.Role) &&
            (Filter.Query.Length == 0 || string.Join(' ', member.Name, member.Email, member.UserId).Contains(Filter.Query, StringComparison.OrdinalIgnoreCase))).OrderBy(member => member.Name).ThenBy(member => member.UserId)];
        public int MemberPages => Math.Max(1, (MatchingMembers.Count + 9) / 10);
        public int MemberPage => Math.Clamp(Filter.Page, 1, MemberPages);
        public int MemberTotal => Access?.Members.Count ?? 0;
        public IReadOnlyList<MemberRecord> MemberRows => [.. MatchingMembers.Skip((MemberPage - 1) * 10).Take(10)];
        public string MemberUrl(int page)
        {
            return "/organization?tab=members&q=" + Uri.EscapeDataString(Filter.Query) + "&role=" + Uri.EscapeDataString(Filter.Role) + (page > 1 ? "&page=" + page : "");
        }

        public IReadOnlyList<InvitationRow> InvitationRows
        {
            get
            {
                var now = Filter.Now ?? DateTimeOffset.UtcNow;
                var rows = (Access?.Invitations ?? []).Select(invite => new InvitationRow(invite.Id, invite.Email, invite.Role,
                    invite.Status == "pending" && invite.ExpiresAt != DateTimeOffset.MinValue && invite.ExpiresAt <= now ? "expired" : invite.Status,
                    // An expired invitation stays manageable: an owner or admin can replace or cancel it.
                    invite.CreatedAt, invite.ExpiresAt, invite.Status is "pending" or "expired")).ToDictionary(invite => invite.Id);
                return [.. rows.Values.OrderBy(invite => !invite.Open).ThenBy(invite => invite.Email, StringComparer.Ordinal)];
            }
        }
        public int InvitationPages => Math.Max(1, (InvitationRows.Count + 9) / 10);
        public int InvitationPage => Math.Clamp(Filter.Page, 1, InvitationPages);
        public IReadOnlyList<InvitationRow> InvitationPageRows => [.. InvitationRows.Skip((InvitationPage - 1) * 10).Take(10)];
        public string InvitationUrl(int page)
        {
            return "/organization?tab=invitations" + (page > 1 ? "&page=" + page : "");
        }

        public static string RoleLabel(string role)
        {
            return role.Length == 0 ? "Unknown" : char.ToUpperInvariant(role[0]) + role[1..];
        }

        public static string RoleFilterLabel(string role)
        {
            return role switch { "all" => "All roles", "owner" => "Organization owner", _ => RoleLabel(role) };
        }

        public static string InvitationTone(string status)
        {
            return status switch { "pending" => "warning", "accepted" => "success", "declined" => "danger", _ => "neutral" };
        }

        public IReadOnlyList<AuditRecord> AuditMatches => Filter.ApplyAudit(Access?.AuditEvents ?? [], Access?.AuditRetentionDays);
        public int AuditPages => Math.Max(1, (AuditMatches.Count + 9) / 10);
        public int AuditPage => Math.Clamp(Filter.Page, 1, AuditPages);
        public IReadOnlyList<AuditRecord> AuditRows => [.. AuditMatches.Skip((AuditPage - 1) * 10).Take(10)];
    }

    public sealed record OrganizationMemberModel(OrganizationPage Page, MemberRecord Member)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Member.Name) ? Member.Email : Member.Name.Trim();
        public string Initials => string.Concat(DisplayName.Split([' ', '.', '@', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Take(2).Select(part => part.EnumerateRunes().First().ToString().ToUpperInvariant()));
        public bool CanEdit => Page.CanManage && Member.UserId != Page.Shell.Context.UserId && Member.Role != "owner";
    }
}
