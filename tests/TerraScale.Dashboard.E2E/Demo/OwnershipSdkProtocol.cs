using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Organizations;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E;

// Test-only HTTP producer for the official management SDK. The access rules,
// seven-day offers/invitations and member demotion follow immutable producer
// 740ad0d6 EngineManagementService.Access.cs, not the separate ts-auth lane.
// Bootstrap scopes are supplied explicitly by the fixture. All later provider
// membership mutations are independent of dashboard projections and billing.
public sealed class OwnershipSdkProtocol(IDashboardState state, TimeProvider clock,
    Func<string, (string UserId, string Email)?> resolveActiveBearer)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Organization> organizations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Invitation> tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> bootstrapOrganizationIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> originalOrganizationIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> bootstrapProjectIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> originalProjectIds = new(StringComparer.Ordinal);
    private readonly TerraScaleIdGenerator ids = new();
    private readonly ConcurrentQueue<string> failures = new();
    private readonly ConcurrentQueue<Request> requests = new();
    private readonly ConcurrentQueue<Acceptance> acceptances = new();
    private int memberReads;
    private int invitationCreates;
    private int invitationAccepts;
    private int transferStarts;
    private int transferConfirms;
    private int transferCancels;

    public void Configure(WebApplicationBuilder builder)
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<IOrganizationAccess, TsOrganizationAccess>());
        builder.Services.Replace(ServiceDescriptor.Singleton<IInvitationGateway, TsInvitationGateway>());
        var original = builder.Services.Last(descriptor => descriptor.ServiceType == typeof(IWorkspaceGateway));
        builder.Services.Remove(original);
        builder.Services.TryAddSingleton<TsWorkspaceGateway>();
        builder.Services.AddSingleton<IWorkspaceGateway>(services => new Workspace(
            (IWorkspaceGateway)(original.ImplementationInstance ?? original.ImplementationFactory?.Invoke(services) ??
                ActivatorUtilities.CreateInstance(services, original.ImplementationType ?? throw new InvalidOperationException("Missing fixture workspace bootstrap."))),
            services.GetRequiredService<TsWorkspaceGateway>(), this));
    }

    public void SeedOrganization(OrganizationRecord value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            // Setup may encounter the same scope on both replicas. It must
            // never overwrite roles after an actual provider mutation.
            if (organizations.ContainsKey(value.Id)) return;
            if (!TerraScaleIds.IsValid(value.Id) || value.Status != "active" || value.Archived || value.DeletionRequested ||
                !value.Members.TryGetValue(value.OwnerUserId, out var owner) || owner.Role != "owner" || owner.Status != "active")
                throw new InvalidOperationException("Ownership setup requires an active, owned fixture organization.");
            var members = new Dictionary<string, Member>(StringComparer.Ordinal);
            foreach (var (id, member) in value.Members.Where(item => item.Value.Status == "active"))
            {
                if (id != member.UserId || !TerraScaleIds.IsValid(id) || Rank(member.Role) < 0)
                    throw new InvalidOperationException("Ownership setup requires canonical principal identities and actual membership roles.");
                members.Add(id, new(id, member.Role, member.CreatedAt));
            }
            organizations.Add(value.Id, new(value.Id, members));
        }
    }

    public async Task<IResult?> DispatchAsync(HttpContext http)
    {
        if (http.Request.Path == "/__test/ownership-sdk/probe" && HttpMethods.IsGet(http.Request.Method)) return Probe();
        var parts = http.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var accepting = parts is ["v1", "management", "invitations", "accept"];
        var scoped = parts.Length >= 5 && parts[0] == "v1" && parts[1] == "management" && parts[2] == "organizations" &&
            parts[4] is "members" or "invitations" or "ownership-transfer" or "audit-retention" or "audit-events";
        if (!accepting && !scoped) return null;
        try
        {
            if (http.Request.Host.Host is not ("api.terrascale.tech" or "gru.api.terrascale.tech")) return Error(403, "forbidden");
            var header = http.Request.Headers.Authorization.ToString();
            var actor = header.StartsWith("Bearer ", StringComparison.Ordinal) ? resolveActiveBearer(header[7..]) : null;
            if (actor is null) return Error(401, "unauthorized");
            if (!TerraScaleIds.IsValid(actor.Value.UserId) || http.Request.Headers["X-TerraBase-User-Id"] != actor.Value.UserId)
                return Error(403, "forbidden");
            if (accepting)
            {
                if (!HttpMethods.IsPost(http.Request.Method)) return Results.StatusCode(405);
                var body = await BodyAsync(http);
                var token = Text(body, "token");
                if (token.Length == 0) return Error(400, "validation_failed");
                if (actor.Value.Email.Length == 0) return Error(403, "forbidden");
                return await AcceptAsync(http, actor.Value, token);
            }
            var organizationId = parts[3];
            if (!TerraScaleIds.IsValid(organizationId)) return Error(400, "validation_failed");
            if (http.Request.Headers["X-TerraBase-Organization-Id"] != organizationId) return Error(403, "forbidden");
            var document = await state.GetAsync(AccountService.OrganizationsCollection, organizationId, http.RequestAborted);
            if (document is null) return Error(404, "not_found");
            var canonical = AccountService.Read<OrganizationRecord>(document);
            if (document.Id != organizationId || canonical.Id != organizationId)
                throw new InvalidOperationException("Ownership scope matches its canonical fixture document.");
            if (canonical.Status != "active" || canonical.Archived || canonical.DeletionRequested) return Error(404, "not_found");
            SeedOrganization(canonical);
            var payload = HttpMethods.IsPost(http.Request.Method) || HttpMethods.IsPut(http.Request.Method) ? await BodyAsync(http) : default;
            lock (gate)
            {
                var organization = organizations[organizationId];
                if (!organization.Members.TryGetValue(actor.Value.UserId, out var member)) return Error(404, "not_found");
                requests.Enqueue(new(http.Request.Method, http.Request.Path.Value!, actor.Value.UserId, organizationId));
                if (parts[4] == "members") return Members(http, parts, organization, member, payload);
                if (parts[4] == "invitations") return Invitations(http, parts, organization, member, payload);
                if (parts[4] == "ownership-transfer") return Transfer(http, parts, organization, member, payload);
                if (Rank(member.Role) < Rank("admin")) return Error(403, "forbidden");
                if (parts.Length != 5) return Error(404, "not_found");
                if (parts[4] == "audit-events" && HttpMethods.IsGet(http.Request.Method))
                {
                    var cutoff = clock.GetUtcNow().AddDays(-organization.RetentionDays);
                    return Page(http, organization.Audit.Select((value, index) => (Key(index), (object)value)),
                        value => ((Audit)value).OccurredAt >= cutoff);
                }
                if (parts[4] == "audit-retention")
                {
                    if (HttpMethods.IsGet(http.Request.Method)) return Results.Json(new { days = organization.RetentionDays });
                    if (HttpMethods.IsPut(http.Request.Method))
                    {
                        if (!payload.TryGetProperty("days", out var days) || !days.TryGetInt32(out var count) || count is < 1 or > 90)
                            return Error(400, "validation_failed");
                        organization.RetentionDays = count;
                        Record(organization, member.UserId, "settings.audit_retention_updated", null, count.ToString(CultureInfo.InvariantCulture));
                        return Results.Json(new { days = count });
                    }
                }
                return Results.StatusCode(405);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException or ArgumentException)
        { failures.Enqueue(error.Message); return Error(500, "test_contract_failure"); }
    }

    private IResult Members(HttpContext http, string[] parts, Organization org, Member actor, JsonElement body)
    {
        if (parts.Length == 5 && HttpMethods.IsGet(http.Request.Method))
        {
            memberReads++;
            return Page(http, org.Members.Values.OrderBy(value => value.UserId, StringComparer.Ordinal).Select(value => (value.UserId, (object)value)));
        }
        if (Rank(actor.Role) < Rank("admin")) return Error(403, "forbidden");
        if (parts.Length < 6 || !TerraScaleIds.IsValid(parts[5])) return Error(400, "validation_failed");
        if (!org.Members.TryGetValue(parts[5], out var target)) return Error(404, "not_found");
        if (target.Role == "owner" && actor.Role != "owner") return Error(403, "forbidden");
        if (parts.Length == 7 && parts[6] == "role" && HttpMethods.IsPut(http.Request.Method))
        {
            var role = Text(body, "role");
            if (Rank(role) < 0) return Error(400, "validation_failed");
            if (role == "owner" && actor.Role != "owner") return Error(403, "forbidden");
            if (target.Role == "owner" && role != "owner" && org.Members.Values.Count(value => value.Role == "owner") == 1)
                return Error(409, "last_owner");
            if (target.Role == role) return Results.Json(target);
            var lostOwnership = target.Role == "owner" && role != "owner";
            target = target with { Role = role }; org.Members[target.UserId] = target;
            if (lostOwnership) DropTransfer(org, target.UserId);
            Record(org, actor.UserId, "member.role_changed", target.UserId, role);
            return Results.Json(target);
        }
        if (parts.Length == 6 && HttpMethods.IsDelete(http.Request.Method))
        {
            if (target.Role == "owner" && org.Members.Values.Count(value => value.Role == "owner") == 1) return Error(409, "last_owner");
            org.Members.Remove(target.UserId); DropTransfer(org, target.UserId);
            Record(org, actor.UserId, "member.removed", target.UserId, null);
            return Results.NoContent();
        }
        return Results.StatusCode(405);
    }

    private IResult Invitations(HttpContext http, string[] parts, Organization org, Member actor, JsonElement body)
    {
        if (Rank(actor.Role) < Rank("admin")) return Error(403, "forbidden");
        if (parts.Length == 5 && HttpMethods.IsGet(http.Request.Method))
            return Page(http, org.Invitations.Select((invitation, index) => (Key(index), Wire(invitation))));
        if (parts.Length == 5 && HttpMethods.IsPost(http.Request.Method))
        {
            var email = Text(body, "email").Trim().ToLowerInvariant();
            var role = Text(body, "role"); if (role.Length == 0) role = "member";
            if (email.Length > 254 || !MailAddress.TryCreate(email, out var address) || !address.Address.Equals(email, StringComparison.OrdinalIgnoreCase) || Rank(role) < 0)
                return Error(400, "validation_failed");
            if (role == "owner" && actor.Role != "owner") return Error(403, "forbidden");
            var now = clock.GetUtcNow();
            if (org.Invitations.Any(value => value.Status == "pending" && value.Email == email && value.ExpiresAt > now)) return Error(409, "invitation_pending");
            var id = ids.NewId();
            var token = org.Id + "." + id + "." + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var invitation = new Invitation(id, org.Id, email, role, actor.UserId, now, now.AddDays(7), Digest(token));
            org.Invitations.Add(invitation); tokens.Add(invitation.TokenHash, invitation);
            invitationCreates++;
            Record(org, actor.UserId, "invitation.created", email, role);
            return Results.Json(new { invitation = Wire(invitation), token }, statusCode: 201);
        }
        if (parts.Length == 6 && HttpMethods.IsDelete(http.Request.Method))
        {
            var invitation = org.Invitations.FirstOrDefault(value => value.Id == parts[5]);
            if (invitation is null) return Error(404, "not_found");
            if (invitation.Status != "pending") return Error(409, "invitation_closed");
            invitation.Status = "revoked";
            Record(org, actor.UserId, "invitation.revoked", invitation.Email, null);
            return Results.NoContent();
        }
        return Results.StatusCode(405);
    }

    private async Task<IResult> AcceptAsync(HttpContext http, (string UserId, string Email) actor, string token)
    {
        Invitation? invitation;
        lock (gate) invitation = tokens.GetValueOrDefault(Digest(token));
        if (invitation is null) return Error(404, "not_found");
        var document = await state.GetAsync(AccountService.OrganizationsCollection, invitation.OrganizationId, http.RequestAborted);
        if (document is null) return Error(404, "not_found");
        var canonical = AccountService.Read<OrganizationRecord>(document);
        if (document.Id != canonical.Id || canonical.Id != invitation.OrganizationId || canonical.Status != "active" || canonical.Archived || canonical.DeletionRequested)
            return Error(404, "not_found");
        lock (gate)
        {
            if (invitation.Status != "pending") return Error(409, "invitation_closed");
            if (!actor.Email.Equals(invitation.Email, StringComparison.OrdinalIgnoreCase)) return Error(403, "forbidden");
            if (invitation.ExpiresAt <= clock.GetUtcNow()) return Error(409, "expired");
            var org = organizations[invitation.OrganizationId];
            if (org.Members.ContainsKey(actor.UserId)) return Error(409, "already_member");
            var member = new Member(actor.UserId, invitation.Role, clock.GetUtcNow());
            org.Members.Add(member.UserId, member); invitation.Status = "accepted";
            invitationAccepts++;
            requests.Enqueue(new(http.Request.Method, http.Request.Path.Value!, actor.UserId, org.Id));
            acceptances.Enqueue(new(actor.UserId, actor.Email, org.Id, invitation.Id, invitation.Status));
            Record(org, actor.UserId, "invitation.accepted", invitation.Email, invitation.Role);
            return Results.Json(member);
        }
    }

    private IResult Transfer(HttpContext http, string[] parts, Organization org, Member actor, JsonElement body)
    {
        if (org.Transfer is { } expired && expired.ExpiresAt <= clock.GetUtcNow()) org.Transfer = null;
        if (parts.Length == 5 && HttpMethods.IsGet(http.Request.Method))
            return org.Transfer is null ? Error(404, "not_found") : Results.Json(org.Transfer);
        if (parts.Length == 5 && HttpMethods.IsPost(http.Request.Method))
        {
            if (actor.Role != "owner") return Error(403, "forbidden");
            var id = Text(body, "userId");
            if (!TerraScaleIds.IsValid(id)) return Error(400, "validation_failed");
            if (!org.Members.TryGetValue(id, out var nominee)) return Error(404, "not_found");
            if (nominee.Role == "owner") return Error(409, "already_owner");
            var now = clock.GetUtcNow(); org.Transfer = new(actor.UserId, id, now, now.AddDays(7));
            transferStarts++; Record(org, actor.UserId, "ownership.transfer_started", id, null);
            return Results.Json(org.Transfer, statusCode: 201);
        }
        if (parts.Length == 6 && parts[5] == "confirm" && HttpMethods.IsPost(http.Request.Method))
        {
            var transfer = org.Transfer;
            if (transfer is null) return Error(404, "not_found");
            if (transfer.ToUserId != actor.UserId) return Error(403, "forbidden");
            if (!org.Members.TryGetValue(transfer.FromUserId, out var starter) || starter.Role != "owner") return Error(404, "not_found");
            var owner = actor with { Role = "owner" };
            org.Transfer = null; org.Members[owner.UserId] = owner; org.Members[starter.UserId] = starter with { Role = "member" };
            transferConfirms++; Record(org, actor.UserId, "ownership.transfer_confirmed", starter.UserId, null);
            return Results.Json(owner);
        }
        if (parts.Length == 5 && HttpMethods.IsDelete(http.Request.Method))
        {
            if (org.Transfer is not { } transfer) return Error(404, "not_found");
            if (actor.Role != "owner" && transfer.ToUserId != actor.UserId) return Error(403, "forbidden");
            org.Transfer = null; transferCancels++; Record(org, actor.UserId, "ownership.transfer_cancelled", transfer.ToUserId, null);
            return Results.NoContent();
        }
        return Results.StatusCode(405);
    }

    private IResult Probe()
    {
        lock (gate) return Results.Json(new
        {
            memberReads,
            invitationCreates,
            invitationAccepts,
            transferStarts,
            transferConfirms,
            transferCancels,
            failures = failures.ToArray(),
            requests = requests.ToArray(),
            acceptances = acceptances.ToArray(),
            organizations = organizations.Values.Select(org => new
            {
                id = org.Id,
                members = org.Members.Values.ToArray(),
                pendingTransfer = org.Transfer,
                invitations = org.Invitations.Select(Wire).ToArray()
            }).ToArray()
        });
    }
    private IResult Page(HttpContext http, IEnumerable<(string Key, object Value)> source, Func<object, bool>? retain = null)
    {
        var limit = 20;
        var requested = http.Request.Query["limit"].ToString();
        if (requested.Length != 0 && (!int.TryParse(requested, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100))
            return Error(400, "validation_failed");
        var after = http.Request.Query["after"].ToString();
        var rows = source.Where(value => after.Length == 0 || string.CompareOrdinal(value.Key, after) > 0).Take(limit + 1).ToArray();
        var page = rows.Take(limit).Select(value => value.Value);
        if (retain is not null) page = page.Where(retain);
        return Results.Json(new { items = page.ToArray(), next = rows.Length > limit ? rows[limit - 1].Key : null });
    }
    private void Record(Organization org, string actor, string action, string? target, string? detail) =>
        org.Audit.Add(new(ids.NewId(), actor, action, target, detail, clock.GetUtcNow()));
    private static void DropTransfer(Organization org, string user) { if (org.Transfer is { } transfer && (transfer.FromUserId == user || transfer.ToUserId == user)) org.Transfer = null; }
    private static string Key(int index) => index.ToString("D20", CultureInfo.InvariantCulture);
    private static int Rank(string role) => role switch { "viewer" => 0, "member" => 1, "admin" => 2, "owner" => 3, _ => -1 };
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";
    private static async Task<JsonElement> BodyAsync(HttpContext http)
    { if (http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false) return JsonSerializer.SerializeToElement(new { }); using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted); return body.RootElement.Clone(); }
    private static string Digest(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static object Wire(Invitation value) => new { value.Id, value.Email, value.Role, value.InvitedBy, value.CreatedAt, value.ExpiresAt, value.Status };
    private static IResult Error(int status, string code) => Results.Json(new { error = new { code, message = "Management request refused.", fix = "Check the authenticated organization scope and current membership." } }, statusCode: status);
    private sealed record Member(string UserId, string Role, DateTimeOffset JoinedAt);
    private sealed record Offer(string FromUserId, string ToUserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
    private sealed record Audit(string Id, string Actor, string Action, string? Target, string? Detail, DateTimeOffset OccurredAt);
    private sealed record Request(string Method, string Path, string UserId, string OrganizationId);
    private sealed record Acceptance(string UserId, string Email, string OrganizationId, string InvitationId, string Status);
    private sealed class Invitation(string id, string org, string email, string role, string actor, DateTimeOffset created, DateTimeOffset expires, string tokenHash)
    {
        public string Id { get; } = id;
        public string OrganizationId { get; } = org;
        public string Email { get; } = email;
        public string Role { get; } = role;
        public string InvitedBy { get; } = actor;
        public DateTimeOffset CreatedAt { get; } = created;
        public DateTimeOffset ExpiresAt { get; } = expires;
        public string TokenHash { get; } = tokenHash;
        public string Status { get; set; } = "pending";
    }
    private sealed class Organization(string id, Dictionary<string, Member> members)
    {
        public string Id { get; } = id;
        public Dictionary<string, Member> Members { get; } = members;
        public List<Invitation> Invitations { get; } = [];
        public List<Audit> Audit { get; } = [];
        public Offer? Transfer { get; set; }
        public int RetentionDays { get; set; } = 90;
    }
    private sealed class Workspace(IWorkspaceGateway bootstrap, TsWorkspaceGateway real, OwnershipSdkProtocol protocol) : IWorkspaceGateway
    {
        public async Task<OrganizationRecord> EnsurePersonalAsync(SessionRecord session, CancellationToken ct)
        { var org = protocol.CanonicalBootstrap(await bootstrap.EnsurePersonalAsync(session, ct)); protocol.SeedOrganization(org); return org; }
        public async Task<OrganizationRecord> CreateOrganizationAsync(SessionRecord session, string name, CancellationToken ct)
        { var org = protocol.CanonicalBootstrap(await bootstrap.CreateOrganizationAsync(session, name, ct)); protocol.SeedOrganization(org); return org; }
        public async Task<ProjectRecord> CreateProjectAsync(SessionRecord session, string org, string name, CancellationToken ct) =>
            protocol.CanonicalProject(await bootstrap.CreateProjectAsync(session, protocol.OriginalOrganization(org), name, ct), org);
        public Task<string> RoleAsync(SessionRecord session, string org, CancellationToken ct) => real.RoleAsync(session, org, ct);
        public Task RenameOrganizationAsync(SessionRecord session, string org, string name, CancellationToken ct) => bootstrap.RenameOrganizationAsync(session, protocol.OriginalOrganization(org), name, ct);
        public Task RenameProjectAsync(SessionRecord session, string org, string project, string name, CancellationToken ct) => bootstrap.RenameProjectAsync(session, protocol.OriginalOrganization(org), protocol.OriginalProject(project), name, ct);
    }

    // Alias only new fixture bootstrap outputs before they reach dashboard
    // persistence. Never rewrite an existing canonical document or admit a
    // legacy fixture ID to an official management SDK route.
    private OrganizationRecord CanonicalBootstrap(OrganizationRecord source)
    {
        lock (gate)
        {
            var copy = JsonSerializer.Deserialize<OrganizationRecord>(JsonSerializer.Serialize(source))!;
            if (!bootstrapOrganizationIds.TryGetValue(source.Id, out var id))
            { id = ids.NewId(); bootstrapOrganizationIds.Add(source.Id, id); originalOrganizationIds.Add(id, source.Id); }
            copy.Id = id;
            copy.Projects = source.Projects.Values.Select(project => CanonicalProject(project, id)).ToDictionary(project => project.Id);
            return copy;
        }
    }
    private ProjectRecord CanonicalProject(ProjectRecord source, string organizationId)
    {
        lock (gate)
        {
            var copy = JsonSerializer.Deserialize<ProjectRecord>(JsonSerializer.Serialize(source))!;
            if (!bootstrapProjectIds.TryGetValue(source.Id, out var id))
            { id = ids.NewId(); bootstrapProjectIds.Add(source.Id, id); originalProjectIds.Add(id, source.Id); }
            copy.Id = id; copy.OrganizationId = organizationId;
            return copy;
        }
    }
    private string OriginalOrganization(string id)
    { lock (gate) return originalOrganizationIds.GetValueOrDefault(id) ?? throw new InvalidOperationException("Unknown fixture bootstrap organization."); }
    private string OriginalProject(string id)
    { lock (gate) return originalProjectIds.GetValueOrDefault(id) ?? throw new InvalidOperationException("Unknown fixture bootstrap project."); }
}
