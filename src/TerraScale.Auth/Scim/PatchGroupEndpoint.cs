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

internal sealed class PatchGroupEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPatch("/scim/{directoryId}/Groups/{groupId}", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, string groupId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct)
    {
        var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
        if (gate.Failure is { } failure)
            return failure;
        var directory = gate.Directory!;

        var (body, bodyFailure) = await ReadBodyAsync<ScimPatchBody>(request, ct);
        if (body is null)
            return bodyFailure ?? ScimError(400, "a JSON body is required");
        if (body.Operations is not { Count: > 0 })
            return ScimError(400, "Operations with at least one replace operation are required", "invalidValue");

        var groups = new ScimGroups(client, store.Database);
        var group = await FindScimGroupAsync(groups, directory.Id, groupId, ct);
        if (group is null)
            return ScimError(404, $"no group with id '{groupId}'");

        var updated = group;
        async Task<IResult?> ReplaceGroupAttributeAsync(string attribute, JsonElement value)
        {
            if (attribute.Equals("displayName", StringComparison.OrdinalIgnoreCase))
            {
                if (value.ValueKind != JsonValueKind.String)
                    return ScimError(400, "replacing 'displayName' requires a string value", "invalidValue");
                if (string.IsNullOrWhiteSpace(value.GetString()))
                    return ScimError(400, "displayName cannot be empty", "invalidValue");
                updated = updated with { DisplayName = value.GetString()! };
                return null;
            }
            if (attribute.Equals("members", StringComparison.OrdinalIgnoreCase))
            {
                if (value.ValueKind != JsonValueKind.Array)
                    return ScimError(400, "replacing 'members' requires an array value", "invalidValue");
                if (await ParseMembersAsync(store, directory, value.Deserialize<List<ScimMember>>(Json), ct) is not { } memberIds)
                    return ScimError(400, "members must reference existing users by their id", "invalidValue");
                updated = updated with { MemberUserIds = [.. memberIds] };
                return null;
            }
            return ScimError(400, $"unsupported patch path '{attribute}'; only 'displayName' and 'members' are supported", "invalidPath");
        }

        foreach (var operation in body.Operations)
        {
            var op = (operation.Op ?? "replace").Trim().ToLowerInvariant();
            var path = operation.Path?.Trim();
            if (op != "replace")
                return ScimError(400, $"only 'replace' operations are supported, got '{op}'", "invalidValue");
            if (path is null)
            {
                // RFC 7644 3.5.2: without a path, the value names the attributes to replace on the resource itself.
                if (operation.Value is not { ValueKind: JsonValueKind.Object } attributes)
                    return ScimError(400, "a patch operation without 'path' requires an object value of attributes to replace", "invalidValue");
                foreach (var attribute in attributes.EnumerateObject())
                    if (await ReplaceGroupAttributeAsync(attribute.Name, attribute.Value) is { } nestedFailure)
                        return nestedFailure;
            }
            else if (await ReplaceGroupAttributeAsync(path, operation.Value ?? default) is { } opFailure)
            {
                return opFailure;
            }
        }

        updated = updated with { UpdatedAt = Now() };
        await groups.UpsertAsync(updated, ct);
        await AuditAsync(store, directory, "directory.group_updated", new() { ["group_id"] = group.Id }, ct);
        return ScimJson(GroupView(directory.Id, updated));
    }
}
