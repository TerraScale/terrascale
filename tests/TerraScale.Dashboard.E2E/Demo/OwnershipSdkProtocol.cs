using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Organizations;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E
{
    // Test-only ts-auth platform producer at https://auth.terrascale.tech/v1/terrascale/. Routes, statuses
    // and snake_case bodies follow the TerraScale.DotNet 0.0.3 contracts/auth routes.json and dtos.json;
    // the per-test TestWorkspace is the registry, so the production TsAuthGateway reaches it only through
    // the official SDK over HTTP. Every call is authorized by the caller's own active sign-in bearer.
    sealed class OwnershipSdkProtocol(TestWorkspace workspace, Func<string, (string UserId, string Email)?> resolveActiveBearer)
    {
        private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
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
            builder.Services.Replace(ServiceDescriptor.Singleton<ITsAuthPlatform>(services => services.GetRequiredService<TsAuthGateway>()));
            builder.Services.Replace(ServiceDescriptor.Singleton<IOrganizationAccess, TsOrganizationAccess>());
        }

        // Fixture organizations are written straight into the shared registry; this only checks they are there.
        public void SeedOrganization(OrganizationRecord value)
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (workspace.Gate)
            {
                if (!workspace.Organizations.TryGetValue(value.Id, out var registered) || registered.Members.GetValueOrDefault(registered.OwnerUserId)?.Role != "owner")
                {
                    throw new InvalidOperationException("Ownership setup requires an owned organization in the ts-auth registry.");
                }
            }
        }

        public async Task<IResult?> DispatchAsync(HttpContext http)
        {
            if (http.Request.Path == "/__test/ownership-sdk/probe" && HttpMethods.IsGet(http.Request.Method))
            {
                return await ProbeAsync(http.RequestAborted);
            }

            var path = http.Request.Path.Value!;
            if (!path.StartsWith("/v1/terrascale/", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                Require(http.Request.Host.Host == "auth.terrascale.tech", "ts-auth platform calls use the trusted auth origin.");
                Require(http.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.Ordinal), "Platform calls accept JSON.");
                var header = http.Request.Headers.Authorization.ToString();
                var bearer = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
                if (bearer.Length == 0 || resolveActiveBearer(bearer) is not { } caller)
                {
                    return Error(401, "unauthorized");
                }

                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[2..];
                requests.Enqueue(new(http.Request.Method, path, caller.UserId, parts is ["organizations", var scope, ..] ? scope : ""));
                var session = new SessionRecord(caller.UserId, "", caller.Email, bearer, null, default);
                var body = await BodyAsync(http);
                return await RouteAsync(http, parts, session, body);
            }
            catch (AccountAccessException error)
            {
                // TestWorkspace raises the refusals the dashboard expects from TsAuthGateway; 422 is its view of ts-auth's 400.
                var status = error.StatusCode == 422 ? 400 : error.StatusCode;
                return Error(status, error.Code.Length > 0 ? error.Code : status switch { 401 => "unauthorized", 404 => "not_found", 409 => "conflict", _ => "forbidden" });
            }
            catch (Exception error) when (error is InvalidOperationException or JsonException or FormatException)
            { failures.Enqueue(error.Message); return Error(500, "test_contract_failure"); }
        }

        private async Task<IResult> RouteAsync(HttpContext http, string[] parts, SessionRecord session, JsonElement body)
        {
            var method = http.Request.Method;
            var token = http.RequestAborted;
            string Field(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
            switch (parts, method)
            {
                case (["account", "providers"], "GET"):
                    return Json(new { providers = Array.Empty<string>() });
                case (["organizations"], "GET"):
                    return Page(http, await workspace.ListOrganizationsAsync(session, token));
                case (["organizations"], "POST"):
                    return Json(await workspace.CreateOrganizationAsync(session, Name(Field("name")), token), 201);
                case (["invitations", "lookup"], "POST"):
                    return await workspace.LookupInvitationAsync(session, Token(Field("token")), token) is { } lookup ? Json(lookup) : Error(404, "not_found");
                case (["invitations", "accept"], "POST"):
                    var accepted = await workspace.AcceptInvitationAsync(session, Token(Field("token")), token);
                    Interlocked.Increment(ref invitationAccepts);
                    acceptances.Enqueue(new(session.UserId, session.Email, accepted.OrganizationId, accepted.Role));
                    return Json(accepted);
                case (["invitations", "decline"], "POST"):
                    await workspace.DeclineInvitationAsync(session, Token(Field("token")), token);
                    return Results.NoContent();
                // A ts-auth without two-factor routes: the dashboard reports the status as unknown.
                case (["account", "mfa"], "GET"):
                    return Error(404, "not_found");
            }
            if (parts is not ["organizations", var organization, ..] || !TerraScaleIds.IsValid(organization))
            {
                return Unknown(method, parts);
            }

            switch (parts[2..], method)
            {
                case ([], "GET"):
                    return Json((await workspace.ListOrganizationsAsync(session, token)).Single(item => item.Id == organization));
                case ([], "PATCH"):
                    await workspace.RenameOrganizationAsync(session, organization, Name(Field("name")), token);
                    return Json((await workspace.ListOrganizationsAsync(session, token)).Single(item => item.Id == organization));
                case (["leave"], "POST"):
                    await workspace.LeaveOrganizationAsync(session, organization, token);
                    return Results.NoContent();
                case (["members"], "GET"):
                    Interlocked.Increment(ref memberReads);
                    return Page(http, await workspace.ListMembersAsync(session, organization, token));
                case (["members", var user, "role"], "PUT") when TerraScaleIds.IsValid(user):
                    await workspace.ChangeMemberRoleAsync(session, organization, user, Field("role"), token);
                    return Json(await MemberAsync(session, organization, user, token));
                case (["members", var user], "DELETE") when TerraScaleIds.IsValid(user):
                    await workspace.RemoveMemberAsync(session, organization, user, token);
                    return Results.NoContent();
                case (["projects"], "GET"):
                    return Page(http, await workspace.ListProjectsAsync(session, organization, token));
                case (["projects"], "POST"):
                    return Json(await workspace.CreateProjectAsync(session, organization, Name(Field("name")), token), 201);
                case (["projects", var project], "PATCH") when TerraScaleIds.IsValid(project):
                    await workspace.RenameProjectAsync(session, organization, project, Name(Field("name")), token);
                    return Json((await workspace.ListProjectsAsync(session, organization, token)).Single(item => item.Id == project));
                case (["projects", var project], "DELETE") when TerraScaleIds.IsValid(project):
                    await workspace.DeleteProjectAsync(session, organization, project, token);
                    return Results.NoContent();
                case (["invitations"], "GET"):
                    return Page(http, await workspace.ListInvitationsAsync(session, organization, token));
                case (["invitations"], "POST"):
                    Require(Field("project_id").Length == 0, "Organization invitations carry no project.");
                    var created = await workspace.CreateInvitationAsync(session, organization, Field("email"), Field("role"), token);
                    Interlocked.Increment(ref invitationCreates);
                    return Json(created, 201);
                case (["invitations", var invitation], "DELETE") when TerraScaleIds.IsValid(invitation):
                    await workspace.RevokeInvitationAsync(session, organization, invitation, token);
                    return Results.NoContent();
                case (["ownership-transfer"], "GET"):
                    return await workspace.GetOwnershipTransferAsync(session, organization, token) is { } current ? Json(current) : Error(404, "not_found");
                case (["ownership-transfer"], "POST"):
                    var nominee = Field("user_id");
                    if (!TerraScaleIds.IsValid(nominee))
                    {
                        return Error(400, "invalid_request");
                    }

                    await workspace.StartOwnershipTransferAsync(session, organization, nominee, token);
                    Interlocked.Increment(ref transferStarts);
                    return Json((await workspace.GetOwnershipTransferAsync(session, organization, token))!, 201);
                case (["ownership-transfer", "confirm"], "POST"):
                    await workspace.ConfirmOwnershipTransferAsync(session, organization, token);
                    Interlocked.Increment(ref transferConfirms);
                    return Json(await MemberAsync(session, organization, session.UserId, token));
                case (["ownership-transfer"], "DELETE"):
                    await workspace.CancelOwnershipTransferAsync(session, organization, token);
                    Interlocked.Increment(ref transferCancels);
                    return Results.NoContent();
                case (["audit-events"], "GET"):
                    return Page(http, await workspace.ListAuditEventsAsync(session, organization, token));
                default:
                    return Unknown(method, parts);
            }
        }

        private async Task<TerraScaleAuthPlatformMember> MemberAsync(SessionRecord session, string organization, string user, CancellationToken token)
        {
            return (await workspace.ListMembersAsync(session, organization, token)).Single(member => member.UserId == user);
        }

        private async Task<IResult> ProbeAsync(CancellationToken token)
        {
            OrganizationRecord[] registry;
            lock (workspace.Gate)
            {
                registry = workspace.Organizations.Values.Select(TestWorkspace.Clone).ToArray();
            }

            var organizations = new List<object>();
            foreach (var org in registry)
            {
                // Read the current offer the way its owner would; the probe adds no authority of its own.
                var owner = new SessionRecord(org.OwnerUserId, "", "", "", null, default);
                organizations.Add(new
                {
                    id = org.Id,
                    members = org.Members.Values.Where(member => member.Status == "active")
                        .Select(member => new { userId = member.UserId, email = member.Email, role = member.Role }).ToArray(),
                    pendingTransfer = await workspace.GetOwnershipTransferAsync(owner, org.Id, token) is { } offer
                        ? new { fromUserId = offer.FromUserId, toUserId = offer.ToUserId, createdAt = offer.CreatedAt, expiresAt = offer.ExpiresAt }
                        : null
                });
            }
            return Results.Json(new
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
                organizations
            });
        }

        // Pages follow the producer: limit 1..100 (default 20) and an opaque same-list cursor.
        private static IResult Page<T>(HttpContext http, IReadOnlyList<T> items)
        {
            var query = http.Request.Query;
            var limit = 20;
            if (query.ContainsKey("limit") && (!int.TryParse(query["limit"], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100))
            {
                return Error(400, "invalid_request");
            }

            var start = 0;
            if (query.ContainsKey("after") && (!int.TryParse(query["after"], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 1 || start > items.Count))
            {
                return Error(400, "invalid_request");
            }

            var page = items.Skip(start).Take(limit).ToArray();
            return Json(new { items = page, next = start + page.Length < items.Count ? (start + page.Length).ToString(CultureInfo.InvariantCulture) : null });
        }

        private IResult Unknown(string method, string[] parts)
        {
            failures.Enqueue("Unexpected platform call " + method + " /" + string.Join('/', parts));
            return Error(404, "not_found");
        }

        private static string Name(string value)
        {
            return value.Trim() is { Length: > 0 and <= 100 } name ? name : throw new AccountAccessException("name is invalid.", 422, "invalid_request");
        }

        private static string Token(string value)
        {
            return value.Length is > 0 and <= 4096 ? value : throw new AccountAccessException("token is invalid.", 422, "invalid_request");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static async Task<JsonElement> BodyAsync(HttpContext http)
        {
            if (http.Request.ContentLength == 0 || string.IsNullOrEmpty(http.Request.ContentType) && http.Request.ContentLength is null)
            {
                return default;
            }

            Require(http.Request.ContentType?.StartsWith("application/json", StringComparison.Ordinal) == true, "Platform bodies are JSON.");
            using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            return body.RootElement.Clone();
        }

        private static IResult Json<T>(T value, int status = 200)
        {
            return Results.Json(value, Wire, statusCode: status);
        }

        private static IResult Error(int status, string code)
        {
            return Results.Json(new { error = new { code, message = "Platform request refused." } }, statusCode: status);
        }

        private sealed record Request(string Method, string Path, string UserId, string OrganizationId);
        private sealed record Acceptance(string UserId, string Email, string OrganizationId, string Role);
    }
}
