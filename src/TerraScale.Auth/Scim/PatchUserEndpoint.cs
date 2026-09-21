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

internal sealed class PatchUserEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPatch("/scim/{directoryId}/Users/{userId}", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, string userId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct)
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

        var user = await FindScimUserAsync(store, directory, userId, ct);
        if (user is null)
            return ScimError(404, $"no user with id '{userId}'");

        var updated = user;
        var supersededEmailKeys = new List<string>();
        async Task<IResult?> ReplaceUserAttributeAsync(string attribute, JsonElement value)
        {
            if (attribute.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return ScimError(400, "replacing 'active' requires a boolean value", "invalidValue");
                var active = value.GetBoolean();
                updated = updated with { Banned = !active, BanReason = active ? null : "directory deprovisioned" };
                return null;
            }
            if (attribute.Equals("userName", StringComparison.OrdinalIgnoreCase))
            {
                if (value.ValueKind != JsonValueKind.String)
                    return ScimError(400, "replacing 'userName' requires a string value", "invalidValue");
                var (rekeyed, superseded, rekeyFailure) = await WithEmailAsync(store, directory.InstanceId, updated, value.GetString()!, ct);
                if (rekeyFailure is not null)
                    return rekeyFailure;
                if (superseded is not null)
                    supersededEmailKeys.Add(superseded);
                updated = rekeyed;
                return null;
            }
            return ScimError(400, $"unsupported patch path '{attribute}'; only 'active' and 'userName' are supported", "invalidPath");
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
                    if (await ReplaceUserAttributeAsync(attribute.Name, attribute.Value) is { } nestedFailure)
                        return nestedFailure;
            }
            else if (await ReplaceUserAttributeAsync(path, operation.Value ?? default) is { } opFailure)
            {
                return opFailure;
            }
        }

        var (written, writeFailure) = await WriteScimUserAsync(store, client, directory, updated with { UpdatedAt = Now() }, supersededEmailKeys, ct);
        if (writeFailure is not null)
            return writeFailure;
        await AuditAsync(store, directory, AuditUpdateType(user, written), new() { ["user_id"] = written.Id }, ct);
        return ScimJson(UserView(directory.Id, written));
    }
}
