namespace TerraScale.Auth.Scim;

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TerraScale.Auth.Api;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Scim.ScimSupport;

internal sealed class CreateUserEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/scim/{directoryId}/Users", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
        if (gate.Failure is { } failure)
            return failure;
        var directory = gate.Directory!;

        var (body, bodyFailure) = await ReadBodyAsync<ScimUserBody>(request, ct);
        if (body is null)
            return bodyFailure ?? ScimError(400, "a JSON body is required");
        if (string.IsNullOrWhiteSpace(body.UserName) || !SessionService.IsValidEmail(body.UserName))
            return ScimError(400, "userName is required and must be a valid email address", "invalidValue");

        User created;
        try
        {
            created = await sessions.CreateUserAsync(directory.InstanceId, body.UserName, null, null, JoinName(body.Name), ct);
        }
        catch (QuotaExceededException)
        {
            return ScimError(403, "the instance has reached its plan's user quota");
        }
        catch (TerraScaleApiException)
        {
            return ScimError(409, "another user with this userName already exists", "uniqueness");
        }

        var externalId = NormalizeExternalId(body.ExternalId);
        if (externalId is not null || body.Active == false)
        {
            var metadata = new Dictionary<string, object>(created.PrivateMetadata);
            if (externalId is not null)
                metadata["scimExternalId"] = externalId;
            created = created with
            {
                PrivateMetadata = metadata,
                Banned = body.Active == false,
                BanReason = body.Active == false ? "directory deprovisioned" : null,
            };
            await store.UpsertUserAsync(created, ct);
        }

        if (directory.OrgId is { } orgId)
        {
            try
            {
                await store.CreateMembershipAsync(new OrgMembership
                {
                    Id = "mem_" + SecureTokens.RandomToken(20),
                    InstanceId = directory.InstanceId,
                    OrgId = orgId,
                    UserId = created.Id,
                    MemberKey = $"{orgId}:{created.Id}",
                    Role = "member",
                    CreatedAt = Now(),
                }, ct);
            }
            catch (TerraScaleApiException)
            {
                // Already a member — idempotent replay of the same provisioning push.
            }
        }

        var data = new Dictionary<string, object> { ["user_id"] = created.Id };
        if (externalId is not null)
            data["external_id"] = externalId;
        await AuditAsync(store, directory, "directory.user_provisioned", data, ct);
        return ScimCreated(UserView(directory.Id, created), $"/scim/{directory.Id}/Users/{created.Id}");
    }
}
