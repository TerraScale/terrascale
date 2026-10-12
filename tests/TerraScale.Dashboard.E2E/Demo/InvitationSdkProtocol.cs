using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Organizations;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E;

// Composes with the external SDK producer. Unrelated workspace/database routes
// remain in DatabaseSdkScenario; this fixture only supplies invitation HTTP.
public sealed class InvitationSdkProtocol
{
    private readonly ConcurrentDictionary<string, (string OrganizationId, TerraBaseInvitation Invitation)> invitations = new();
    private readonly ConcurrentQueue<string> failures = new();
    private readonly TerraScaleIdGenerator ids = new();
    private IDashboardState state = null!;
    private Func<string, (string UserId, string Email)?> resolveAccessToken = null!;
    private int creates;
    private int deletes;
    private int missingDeletes;

    public void Configure(WebApplicationBuilder builder)
    {
        state = (IDashboardState)builder.Services.Last(item => item.ServiceType == typeof(IDashboardState)).ImplementationInstance!;
        resolveAccessToken = ((TestAuth)builder.Services.Last(item => item.ServiceType == typeof(IAuthGateway)).ImplementationInstance!).ResolveAccessToken;
        builder.Services.Replace(ServiceDescriptor.Singleton<IInvitationGateway, TsInvitationGateway>());
    }

    public async Task<IResult?> DispatchAsync(HttpContext http)
    {
        var path = http.Request.Path.Value!;
        if (path == "/__test/invitation-sdk/probe" && HttpMethods.IsGet(http.Request.Method))
            return Results.Json(new { creates, deletes, missingDeletes, failures = failures.ToArray() });
        if (path == "/__test/invitation-sdk/expire" && HttpMethods.IsPost(http.Request.Method)) return await ExpireAsync(http);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length is not (5 or 6) || parts[0] != "v1" || parts[1] != "management" || parts[2] != "organizations" || parts[4] != "invitations") return null;
        try
        {
            if (http.Request.Host.Host is not ("gru.api.terrascale.tech" or "sfo.api.terrascale.tech")) return Error(403, "forbidden");
            var bearer = http.Request.Headers.Authorization.ToString();
            var admitted = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? resolveAccessToken(bearer[7..]) : null;
            if (admitted is null) return Error(401, "unauthorized");
            var organizationId = parts[3];
            if (!TerraScaleIds.IsValid(organizationId) || http.Request.Headers["X-TerraBase-Organization-Id"] != organizationId ||
                http.Request.Headers["X-TerraBase-User-Id"] != admitted.Value.UserId) return Error(403, "forbidden");
            var organization = await OrganizationAsync(organizationId, http.RequestAborted);
            if (organization.OwnerUserId != admitted.Value.UserId || organization.RoleFor(admitted.Value.UserId) != "owner") return Error(403, "forbidden");
            if (parts.Length == 5 && HttpMethods.IsGet(http.Request.Method))
            {
                if (http.Request.Query["limit"] != "100" || http.Request.Query["after"].ToString().Length != 0)
                    throw new InvalidOperationException("Invitation SDK requests its bounded first page.");
                return Results.Json(new { items = invitations.Values.Where(item => item.OrganizationId == organizationId).Select(item => Wire(item.Invitation)).ToArray(), next = (string?)null });
            }
            if (parts.Length == 5 && HttpMethods.IsPost(http.Request.Method))
            {
                using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                var email = body.RootElement.GetProperty("email").GetString()!;
                if (body.RootElement.GetProperty("role").GetString() != "member") return Error(422, "validation_failed");
                var id = ids.NewId();
                var invitation = new TerraBaseInvitation(id, email, TerraBaseRole.Member, admitted.Value.UserId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));
                if (!invitations.TryAdd(id, (organizationId, invitation))) throw new InvalidOperationException("Invitation identity must be unique.");
                Interlocked.Increment(ref creates);
                return Results.Json(new { invitation = Wire(invitation), token = "sdk-invitation-" + id }, statusCode: 201);
            }
            if (parts.Length == 6 && HttpMethods.IsDelete(http.Request.Method))
            {
                if (!invitations.TryGetValue(parts[5], out var invitation) || invitation.OrganizationId != organizationId)
                { Interlocked.Increment(ref missingDeletes); return Error(404, "not_found"); }
                if (!invitations.TryRemove(parts[5], out _)) return Error(404, "not_found");
                Interlocked.Increment(ref deletes);
                return Results.NoContent();
            }
            throw new InvalidOperationException("Unexpected invitation SDK method.");
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException)
        { failures.Enqueue(error.Message); return Error(500, "test_contract_failure"); }
    }

    private async Task<IResult> ExpireAsync(HttpContext http)
    {
        using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        var organizationId = body.RootElement.GetProperty("organizationId").GetString()!;
        var invitationId = body.RootElement.GetProperty("invitationId").GetString()!;
        var subject = body.RootElement.GetProperty("ownerUserId").GetString()!;
        var command = body.RootElement.GetProperty("command").GetString();
        if (command is not ("expire" or "expire-and-remove" or "remove")) return Error(400, "invalid_command");
        var document = await state.GetAsync(AccountService.OrganizationsCollection, organizationId, http.RequestAborted);
        if (document is null || document.Id != organizationId) return Error(404, "not_found");
        var org = document.Document.Deserialize<OrganizationRecord>()!;
        if (org.Id != organizationId || org.OwnerUserId != subject || org.RoleFor(subject) != "owner" || org.DeletionRequested) return Error(403, "forbidden");
        if (!org.Invitations.TryGetValue(invitationId, out var canonical) || canonical.OrganizationId != organizationId || canonical.Status != "pending" ||
            !invitations.TryGetValue(invitationId, out var remote) || remote.OrganizationId != organizationId) return Error(404, "not_found");
        var expires = DateTimeOffset.UtcNow.AddDays(-1);
        if (command != "remove") canonical.ExpiresAt = expires;
        await state.PutAsync(AccountService.OrganizationsCollection, organizationId, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Matches(document.Version), http.RequestAborted);
        if (command is "expire-and-remove" or "remove") invitations.TryRemove(invitationId, out _);
        else invitations[invitationId] = (organizationId, remote.Invitation with { ExpiresAt = expires });
        return Results.NoContent();
    }

    private async Task<OrganizationRecord> OrganizationAsync(string id, CancellationToken ct)
    {
        var document = await state.GetAsync(AccountService.OrganizationsCollection, id, ct) ?? throw new InvalidOperationException("Invitation SDK scope exists.");
        var organization = document.Document.Deserialize<OrganizationRecord>()!;
        if (document.Id != id || organization.Id != id) throw new InvalidOperationException("Invitation SDK scope matches its canonical document.");
        return organization;
    }
    private static object Wire(TerraBaseInvitation invitation) => new
    {
        invitation.Id,
        invitation.Email,
        role = invitation.Role.ToString().ToLowerInvariant(),
        invitation.InvitedBy,
        invitation.CreatedAt,
        invitation.ExpiresAt
    };
    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);
}
