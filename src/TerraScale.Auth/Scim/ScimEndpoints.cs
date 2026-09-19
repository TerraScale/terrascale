namespace TerraScale.Auth.Scim;

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;
using TerraScale.DotNet;

/// <summary>
/// SCIM 2.0 directory sync (WorkOS Directory Sync parity): a management API
/// for directories and their bearer tokens, plus a per-directory provisioning
/// surface that maps SCIM users/groups onto ts-auth users, org memberships,
/// and audit events (which fan out to webhooks).
/// </summary>
public static class ScimEndpoints
{
    private const string ScimContentType = "application/scim+json";
    private const int MaxResults = 100;

    private const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";
    private const string ListResponseSchema = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    private const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    private const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";
    private const string ServiceProviderConfigSchema = "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig";
    private const string ResourceTypeSchema = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";
    private const string SchemaSchema = "urn:ietf:params:scim:schemas:core:2.0:Schema";

    /// <summary>Only the unescaped simple filter forms: userName/externalId eq "value".</summary>
    private static readonly Regex FilterRegex = new("""^(userName|externalId)\s+eq\s+"([^"]*)"$""", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Maps Scim endpoints.</summary>
    public static void Map(WebApplication app)
    {
        MapManagement(app);
        MapScimUsers(app);
        MapScimGroups(app);
        MapDiscovery(app);
    }

    // ---- Management API (sk_ bearer) ------------------------------------------------

    private static void MapManagement(WebApplication app)
    {
        app.MapPost("/v1/{slug}/directories", async (
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
        {
            var context = await ResolveInstanceAsync(request, slug, resolver, ct);
            if (context is null)
                return ApiResults.Unauthorized();

            var (body, bodyFailure) = await ReadBodyAsync<CreateDirectoryBody>(request, ct);
            if (body is null)
                return bodyFailure ?? ApiResults.BadRequest("invalid_request", "a JSON body is required");

            string? orgId = null;
            if (body.OrgId is { } rawOrgId)
            {
                var organization = await store.GetOrganizationAsync(rawOrgId, ct);
                if (organization is null || organization.InstanceId != context.Instance.Id)
                    return ApiResults.NotFound("organization_not_found", "no organization with this id in this instance");
                orgId = organization.Id;
            }

            ScimDirectory directory;
            string token;
            try
            {
                (directory, token) = await CreateDirectoryAsync(store, context.Instance.Id, orgId, ct);
            }
            catch (TerraScaleApiException)
            {
                return ApiResults.Conflict("token_collision", "the generated token collided; retry the request");
            }
            await AuditManagementAsync(store, context, "directory.created", new() { ["directory_id"] = directory.Id }, ct);
            return Results.Json(new { id = directory.Id, org_id = directory.OrgId, token }, statusCode: 201);
        });

        app.MapGet("/v1/{slug}/directories", async (
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
        {
            var context = await ResolveInstanceAsync(request, slug, resolver, ct);
            if (context is null)
                return ApiResults.Unauthorized();

            // Tokens are sha256-hashed at rest and never listed back.
            var directories = (await store.ListScimDirectoriesAsync(context.Instance.Id, ct))
                .Select(d => new { id = d.Id, org_id = d.OrgId, created_at = d.CreatedAt })
                .ToList();
            return Results.Json(new { data = directories });
        });

        app.MapDelete("/v1/{slug}/directories/{directoryId}", async (
            string slug, string directoryId, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
        {
            var context = await ResolveInstanceAsync(request, slug, resolver, ct);
            if (context is null)
                return ApiResults.Unauthorized();

            var directory = await FindInstanceDirectoryAsync(store, context.Instance.Id, directoryId, ct);
            if (directory is null)
                return ApiResults.NotFound("directory_not_found", "no directory with this id in this instance");

            await store.DeleteScimDirectoryAsync(directory.Id, ct);
            await store.ReleaseUniqueAsync("scim-token", directory.TokenKey, ct);
            await AuditManagementAsync(store, context, "directory.deleted", new() { ["directory_id"] = directory.Id }, ct);
            return Results.NoContent();
        });

        app.MapPost("/v1/{slug}/directories/{directoryId}/rotate", async (
            string slug, string directoryId, HttpRequest request, InstanceResolver resolver, AuthStore store, TerraScaleClient client, CancellationToken ct) =>
        {
            var context = await ResolveInstanceAsync(request, slug, resolver, ct);
            if (context is null)
                return ApiResults.Unauthorized();

            var directory = await FindInstanceDirectoryAsync(store, context.Instance.Id, directoryId, ct);
            if (directory is null)
                return ApiResults.NotFound("directory_not_found", "no directory with this id in this instance");

            var token = "scim_" + SecureTokens.RandomToken(40);
            var tokenKey = SecureTokens.Sha256Hex(token);
            try
            {
                await store.ClaimUniqueAsync("scim-token", tokenKey, directory.Id, ct);
            }
            catch (TerraScaleApiException)
            {
                return ApiResults.Conflict("token_collision", "the generated token collided; retry the request");
            }
            var rotated = directory with { TokenKey = tokenKey };
            await client.Documents.PutAsync(store.Database, "scim_directories", rotated.Id, rotated, cancellationToken: ct);
            await store.ReleaseUniqueAsync("scim-token", directory.TokenKey, ct);

            await AuditManagementAsync(store, context, "directory.token_rotated", new() { ["directory_id"] = directory.Id }, ct);
            return Results.Json(new { id = rotated.Id, org_id = rotated.OrgId, token });
        });
    }

    // ---- SCIM users -----------------------------------------------------------------

    private static void MapScimUsers(WebApplication app)
    {
        app.MapGet("/scim/{directoryId}/Users", async (
            string directoryId, HttpRequest request, AuthStore store, CancellationToken ct,
            string? filter, int? count, int? startIndex) =>
        {
            var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
            if (gate.Failure is { } failure)
                return failure;
            var directory = gate.Directory!;

            var (offset, pageSize) = Paginate(startIndex, count);

            List<User> matched;
            if (filter is null)
            {
                matched = (await ListInstanceUsersAsync(store, directory.InstanceId, ct)).Where(u => !u.Deleted).ToList();
            }
            else if (ParseFilter(filter) is not { } parsed)
            {
                return ScimError(400, "only simple filters of the form 'userName eq \"value\"' or 'externalId eq \"value\"' are supported", "invalidFilter");
            }
            else if (parsed.Attribute == "username")
            {
                var user = await store.FindUserByEmailAsync(directory.InstanceId, SessionService.NormalizeEmail(parsed.Value), ct);
                matched = user is null || user.Deleted ? [] : [user];
            }
            else
            {
                matched = (await ListInstanceUsersAsync(store, directory.InstanceId, ct))
                    .Where(u => !u.Deleted && ExternalId(u) == parsed.Value)
                    .ToList();
            }

            var ordered = matched.OrderBy(u => u.Id, StringComparer.Ordinal).ToList();
            var resources = ordered.Skip(offset - 1).Take(pageSize).Select(u => UserView(directory.Id, u)).ToList();
            return ScimJson(new ScimListResponse(
                Schemas: [ListResponseSchema],
                TotalResults: ordered.Count,
                StartIndex: offset,
                ItemsPerPage: resources.Count,
                Resources: resources));
        });

        app.MapGet("/scim/{directoryId}/Users/{userId}", async (
            string directoryId, string userId, HttpRequest request, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
            if (gate.Failure is { } failure)
                return failure;
            var directory = gate.Directory!;

            var user = await FindScimUserAsync(store, directory, userId, ct);
            return user is null
                ? ScimError(404, $"no user with id '{userId}'")
                : ScimJson(UserView(directory.Id, user));
        });

        app.MapPost("/scim/{directoryId}/Users", async (
            string directoryId, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct) =>
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
        });

        app.MapPut("/scim/{directoryId}/Users/{userId}", async (
            string directoryId, string userId, HttpRequest request, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
            if (gate.Failure is { } failure)
                return failure;
            var directory = gate.Directory!;

            var (body, bodyFailure) = await ReadBodyAsync<ScimUserBody>(request, ct);
            if (body is null)
                return bodyFailure ?? ScimError(400, "a JSON body is required");

            var user = await FindScimUserAsync(store, directory, userId, ct);
            if (user is null)
                return ScimError(404, $"no user with id '{userId}'");
            if (string.IsNullOrWhiteSpace(body.UserName) || !SessionService.IsValidEmail(body.UserName))
                return ScimError(400, "userName is required and must be a valid email address", "invalidValue");

            // Replace semantics: absent name/externalId clear, absent active keeps the current state.
            var externalId = NormalizeExternalId(body.ExternalId);
            var metadata = new Dictionary<string, object>(user.PrivateMetadata);
            if (externalId is null)
                metadata.Remove("scimExternalId");
            else
                metadata["scimExternalId"] = externalId;
            var updated = user with
            {
                Name = JoinName(body.Name),
                PrivateMetadata = metadata,
                UpdatedAt = Now(),
            };
            if (body.Active is { } active)
                updated = updated with { Banned = !active, BanReason = active ? null : "directory deprovisioned" };

            var (rekeyed, rekeyFailure) = await WithEmailAsync(store, directory.InstanceId, updated, body.UserName, ct);
            if (rekeyFailure is not null)
                return rekeyFailure;
            updated = rekeyed;

            await store.UpsertUserAsync(updated, ct);
            await AuditAsync(store, directory, AuditUpdateType(user, updated), new() { ["user_id"] = updated.Id }, ct);
            return ScimJson(UserView(directory.Id, updated));
        });

        app.MapPatch("/scim/{directoryId}/Users/{userId}", async (
            string directoryId, string userId, HttpRequest request, AuthStore store, CancellationToken ct) =>
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
                    var (rekeyed, rekeyFailure) = await WithEmailAsync(store, directory.InstanceId, updated, value.GetString()!, ct);
                    if (rekeyFailure is not null)
                        return rekeyFailure;
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

            updated = updated with { UpdatedAt = Now() };
            await store.UpsertUserAsync(updated, ct);
            await AuditAsync(store, directory, AuditUpdateType(user, updated), new() { ["user_id"] = updated.Id }, ct);
            return ScimJson(UserView(directory.Id, updated));
        });

        app.MapDelete("/scim/{directoryId}/Users/{userId}", async (
            string directoryId, string userId, HttpRequest request, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
            if (gate.Failure is { } failure)
                return failure;
            var directory = gate.Directory!;

            var user = await FindScimUserAsync(store, directory, userId, ct);
            if (user is null)
                return ScimError(404, $"no user with id '{userId}'");

            // Soft delete: release the identifier claims so the email frees up for reuse.
            if (user.EmailKey is not null)
                await store.ReleaseUniqueAsync("user-email", user.EmailKey, ct);
            if (user.UsernameKey is not null)
                await store.ReleaseUniqueAsync("user-username", user.UsernameKey, ct);
            await store.UpsertUserAsync(user with
            {
                Deleted = true,
                EmailKey = null,
                UsernameKey = null,
                UpdatedAt = Now(),
            }, ct);

            if (directory.OrgId is { } orgId)
                foreach (var membership in await store.ListMembershipsByUserAsync(user.Id, ct))
                    if (membership.OrgId == orgId)
                        await store.DeleteMembershipAsync(membership.Id, ct);

            await AuditAsync(store, directory, "directory.user_deprovisioned", new() { ["user_id"] = user.Id }, ct);
            return Results.NoContent();
        });
    }

    // ---- SCIM groups ----------------------------------------------------------------

    private static void MapScimGroups(WebApplication app)
    {
        app.MapGet("/scim/{directoryId}/Groups", async (
            string directoryId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct,
            string? filter, int? count, int? startIndex) =>
        {
            var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
            if (gate.Failure is { } failure)
                return failure;
            var directory = gate.Directory!;

            if (filter is not null)
                return ScimError(400, "filters are not supported on the Groups endpoint", "invalidFilter");
            var (offset, pageSize) = Paginate(startIndex, count);

            var groups = new ScimGroups(client, store.Database);
            var ordered = (await groups.ListByDirectoryAsync(directory.Id, ct)).OrderBy(g => g.Id, StringComparer.Ordinal).ToList();
            var resources = ordered.Skip(offset - 1).Take(pageSize).Select(g => GroupView(directory.Id, g)).ToList();
            return ScimJson(new ScimListResponse(
                Schemas: [ListResponseSchema],
                TotalResults: ordered.Count,
                StartIndex: offset,
                ItemsPerPage: resources.Count,
                Resources: resources));
        });

        app.MapGet("/scim/{directoryId}/Groups/{groupId}", async (
            string directoryId, string groupId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct) =>
        {
            var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
            if (gate.Failure is { } failure)
                return failure;
            var directory = gate.Directory!;

            var group = await FindScimGroupAsync(new ScimGroups(client, store.Database), directory.Id, groupId, ct);
            return group is null
                ? ScimError(404, $"no group with id '{groupId}'")
                : ScimJson(GroupView(directory.Id, group));
        });

        app.MapPost("/scim/{directoryId}/Groups", async (
            string directoryId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct) =>
        {
            var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
            if (gate.Failure is { } failure)
                return failure;
            var directory = gate.Directory!;

            var (body, bodyFailure) = await ReadBodyAsync<ScimGroupBody>(request, ct);
            if (body is null)
                return bodyFailure ?? ScimError(400, "a JSON body is required");
            if (string.IsNullOrWhiteSpace(body.DisplayName))
                return ScimError(400, "displayName is required", "invalidValue");
            if (await ParseMembersAsync(store, directory, body.Members, ct) is not { } memberIds)
                return ScimError(400, "members must reference existing users by their id", "invalidValue");

            var now = Now();
            var group = new ScimGroup
            {
                Id = "grp_" + SecureTokens.RandomToken(20),
                InstanceId = directory.InstanceId,
                DirectoryId = directory.Id,
                DisplayName = body.DisplayName.Trim(),
                MemberUserIds = [.. memberIds],
                CreatedAt = now,
                UpdatedAt = now,
            };
            await new ScimGroups(client, store.Database).CreateAsync(group, ct);
            await AuditAsync(store, directory, "directory.group_provisioned", new() { ["group_id"] = group.Id }, ct);
            return ScimCreated(GroupView(directory.Id, group), $"/scim/{directory.Id}/Groups/{group.Id}");
        });

        app.MapPut("/scim/{directoryId}/Groups/{groupId}", async (
            string directoryId, string groupId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct) =>
        {
            var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
            if (gate.Failure is { } failure)
                return failure;
            var directory = gate.Directory!;

            var (body, bodyFailure) = await ReadBodyAsync<ScimGroupBody>(request, ct);
            if (body is null)
                return bodyFailure ?? ScimError(400, "a JSON body is required");

            var groups = new ScimGroups(client, store.Database);
            var group = await FindScimGroupAsync(groups, directory.Id, groupId, ct);
            if (group is null)
                return ScimError(404, $"no group with id '{groupId}'");
            if (string.IsNullOrWhiteSpace(body.DisplayName))
                return ScimError(400, "displayName is required", "invalidValue");
            if (await ParseMembersAsync(store, directory, body.Members, ct) is not { } memberIds)
                return ScimError(400, "members must reference existing users by their id", "invalidValue");

            var updated = group with
            {
                DisplayName = body.DisplayName.Trim(),
                MemberUserIds = [.. memberIds],
                UpdatedAt = Now(),
            };
            await groups.UpsertAsync(updated, ct);
            await AuditAsync(store, directory, "directory.group_updated", new() { ["group_id"] = group.Id }, ct);
            return ScimJson(GroupView(directory.Id, updated));
        });

        app.MapPatch("/scim/{directoryId}/Groups/{groupId}", async (
            string directoryId, string groupId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct) =>
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
        });

        app.MapDelete("/scim/{directoryId}/Groups/{groupId}", async (
            string directoryId, string groupId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct) =>
        {
            var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
            if (gate.Failure is { } failure)
                return failure;
            var directory = gate.Directory!;

            var groups = new ScimGroups(client, store.Database);
            var group = await FindScimGroupAsync(groups, directory.Id, groupId, ct);
            if (group is null)
                return ScimError(404, $"no group with id '{groupId}'");

            await groups.DeleteAsync(group.Id, ct);
            await AuditAsync(store, directory, "directory.group_deprovisioned", new() { ["group_id"] = group.Id }, ct);
            return Results.NoContent();
        });
    }

    // ---- SCIM discovery -------------------------------------------------------------

    private static void MapDiscovery(WebApplication app)
    {
        app.MapGet("/scim/{directoryId}/ServiceProviderConfig", async (
            string directoryId, HttpRequest request, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
            if (gate.Failure is { } failure)
                return failure;
            var directory = gate.Directory!;

            return ScimJson(new
            {
                schemas = new[] { ServiceProviderConfigSchema },
                patch = new { supported = true },
                bulk = new { supported = false, maxOperations = 0, maxPayloadSize = 0 },
                filter = new { supported = true, maxResults = MaxResults },
                changePassword = new { supported = false },
                sort = new { supported = false },
                etag = new { supported = false },
                authenticationSchemes = new[]
                {
                    new { name = "OAuth Bearer Token", description = "The directory's SCIM bearer token", type = "oauthbearertoken", primary = true },
                },
                meta = new { resourceType = "ServiceProviderConfig", location = $"/scim/{directory.Id}/ServiceProviderConfig" },
            });
        });

        app.MapGet("/scim/{directoryId}/ResourceTypes", async (
            string directoryId, HttpRequest request, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
            if (gate.Failure is { } failure)
                return failure;
            var directory = gate.Directory!;

            return ScimJson(new ScimListResponse(
                Schemas: [ListResponseSchema],
                TotalResults: 2,
                StartIndex: 1,
                ItemsPerPage: 2,
                Resources:
                [
                    new { schemas = new[] { ResourceTypeSchema }, id = "User", name = "User", endpoint = $"/scim/{directory.Id}/Users", schema = UserSchema },
                    new { schemas = new[] { ResourceTypeSchema }, id = "Group", name = "Group", endpoint = $"/scim/{directory.Id}/Groups", schema = GroupSchema },
                ]));
        });

        app.MapGet("/scim/{directoryId}/Schemas", async (
            string directoryId, HttpRequest request, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
            if (gate.Failure is { } failure)
                return failure;

            return ScimJson(new ScimListResponse(
                Schemas: [ListResponseSchema],
                TotalResults: 2,
                StartIndex: 1,
                ItemsPerPage: 2,
                Resources:
                [
                    new
                    {
                        schemas = new[] { SchemaSchema },
                        id = UserSchema,
                        name = "User",
                        attributes = SchemaAttributes(
                            ("id", "string", ReadOnly: true),
                            ("userName", "string", ReadOnly: false),
                            ("active", "boolean", ReadOnly: false),
                            ("externalId", "string", ReadOnly: false),
                            ("name", "complex", ReadOnly: false),
                            ("meta", "complex", ReadOnly: true)),
                    },
                    new
                    {
                        schemas = new[] { SchemaSchema },
                        id = GroupSchema,
                        name = "Group",
                        attributes = SchemaAttributes(
                            ("id", "string", ReadOnly: true),
                            ("displayName", "string", ReadOnly: false),
                            ("members", "complex", ReadOnly: false),
                            ("meta", "complex", ReadOnly: true)),
                    },
                ]));
        });
    }

    // ---- Shared plumbing ------------------------------------------------------------

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static IResult ScimJson(object body, int statusCode = 200) =>
        Results.Json(body, contentType: ScimContentType, statusCode: statusCode);

    private static IResult ScimError(int status, string detail, string? scimType = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["schemas"] = new[] { ErrorSchema },
            ["status"] = status.ToString(),
            ["detail"] = detail,
        };
        if (scimType is not null)
            body["scimType"] = scimType;
        // RFC 7644 s2: the provider SHALL advertise its auth scheme via WWW-Authenticate.
        return status == StatusCodes.Status401Unauthorized
            ? new WithHeader(ScimJson(body, status), "WWW-Authenticate", "Bearer")
            : ScimJson(body, status);
    }

    /// <summary>RFC 7644 s3.3: the created resource's URI SHALL ride the Location header.</summary>
    private static IResult ScimCreated(object body, string location) =>
        new WithHeader(ScimJson(body, 201), "Location", location);

    private sealed record WithHeader(IResult Inner, string Name, string Value) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.Headers[Name] = Value;
            await Inner.ExecuteAsync(context);
        }
    }

    /// <summary>RFC 7644 s3.4.2.4: startIndex below 1 reads as 1, negative count reads as 0,
    /// and a count above the server maximum is clamped — none of these is an error.</summary>
    private static (int StartIndex, int PageSize) Paginate(int? startIndex, int? count)
    {
        var start = startIndex is > 0 ? startIndex.Value : 1;
        var size = count is null ? MaxResults : Math.Clamp(count.Value, 0, MaxResults);
        return (start, size);
    }

    private static async Task<(TBody? Body, IResult? Failure)> ReadBodyAsync<TBody>(HttpRequest request, CancellationToken ct) where TBody : class
    {
        try
        {
            var body = await request.ReadFromJsonAsync<TBody>(ct);
            return body is null ? (null, ScimError(400, "a JSON body is required")) : (body, null);
        }
        catch (JsonException)
        {
            return (null, ScimError(400, "the request body is not valid JSON", "invalidSyntax"));
        }
        catch (InvalidOperationException)
        {
            // A body that is not JSON at all (e.g. text/plain) must not surface as a 500.
            return (null, ScimError(400, "the request body must be JSON", "invalidSyntax"));
        }
    }

    /// <summary>Management gate: sk_ bearer that resolves the route's instance.</summary>
    private static async Task<InstanceContext?> ResolveInstanceAsync(HttpRequest request, string slug, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        return context is not null && context.Instance.Slug == slug ? context : null;
    }

    private sealed record ScimGate(ScimDirectory? Directory, IResult? Failure);

    /// <summary>SCIM gate: bearer scim_ token that resolves exactly this route's directory.</summary>
    private static async Task<ScimGate> RequireDirectoryAsync(HttpRequest request, AuthStore store, string directoryId, CancellationToken ct)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return new(null, ScimError(401, "a bearer token is required"));
        var token = authorization["Bearer ".Length..].Trim();
        if (!token.StartsWith("scim_", StringComparison.Ordinal))
            return new(null, ScimError(401, "invalid bearer token"));
        var directory = await store.FindScimDirectoryByTokenAsync(SecureTokens.Sha256Hex(token), ct);
        if (directory is null || directory.Id != directoryId)
            return new(null, ScimError(401, "invalid bearer token for this directory"));
        return new(directory, null);
    }

    /// <summary>Creates the directory and returns its bearer token — the only time the token exists in the clear.</summary>
    private static async Task<(ScimDirectory Directory, string Token)> CreateDirectoryAsync(AuthStore store, string instanceId, string? orgId, CancellationToken ct)
    {
        var token = "scim_" + SecureTokens.RandomToken(40);
        var directory = new ScimDirectory
        {
            Id = "dir_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            OrgId = orgId,
            TokenKey = SecureTokens.Sha256Hex(token),
            CreatedAt = Now(),
        };
        await store.CreateScimDirectoryAsync(directory, ct);
        return (directory, token);
    }

    private static async Task<ScimDirectory?> FindInstanceDirectoryAsync(AuthStore store, string instanceId, string directoryId, CancellationToken ct)
    {
        var directory = await store.GetScimDirectoryAsync(directoryId, ct);
        return directory is null || directory.InstanceId != instanceId ? null : directory;
    }

    private static async Task<User?> FindScimUserAsync(AuthStore store, ScimDirectory directory, string userId, CancellationToken ct)
    {
        var user = await store.GetUserAsync(userId, ct);
        return user is null || user.InstanceId != directory.InstanceId || user.Deleted ? null : user;
    }

    private static async Task<ScimGroup?> FindScimGroupAsync(ScimGroups groups, string directoryId, string groupId, CancellationToken ct)
    {
        var group = await groups.GetAsync(groupId, ct);
        return group is null || group.DirectoryId != directoryId ? null : group;
    }

    /// <summary>
    /// All users of one instance, walking the shared users collection by the raw
    /// page cursor (the store filters in memory); the walk ends only when the
    /// engine returns an empty page, so foreign-user interleave cannot truncate it.
    /// </summary>
    private static async Task<List<User>> ListInstanceUsersAsync(AuthStore store, string instanceId, CancellationToken ct)
    {
        var users = new List<User>();
        string? after = null;
        while (true)
        {
            var (page, next) = await store.ListUsersAsync(instanceId, 1000, after, ct);
            users.AddRange(page);
            if (next is null)
                return users;
            after = next;
        }
    }

    private static (string Attribute, string Value)? ParseFilter(string filter)
    {
        var match = FilterRegex.Match(filter.Trim());
        return match.Success ? (match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value) : null;
    }

    private sealed record RekeyResult(User User, IResult? Failure);

    /// <summary>Claim-first email re-key — the same pattern as the Management user update.</summary>
    private static async Task<RekeyResult> WithEmailAsync(AuthStore store, string instanceId, User current, string email, CancellationToken ct)
    {
        if (!SessionService.IsValidEmail(email))
            return new(current, ScimError(400, "userName must be a valid email address"));
        var normalized = SessionService.NormalizeEmail(email);
        if (string.Equals(normalized, current.Email, StringComparison.Ordinal))
            return new(current, null);
        var newKey = $"{instanceId}:{normalized}";
        var existing = await store.FindUserByEmailAsync(instanceId, normalized, ct);
        if (existing is not null && existing.Id != current.Id)
            return new(current, ScimError(409, "another user with this userName already exists", "uniqueness"));
        try
        {
            await store.ClaimUniqueAsync("user-email", newKey, current.Id, ct);
        }
        catch (TerraScaleApiException)
        {
            return new(current, ScimError(409, "another user with this userName already exists", "uniqueness"));
        }
        if (current.EmailKey is not null)
            await store.ReleaseUniqueAsync("user-email", current.EmailKey, ct);
        // Pending verification links prove the OLD address; they must not verify the new one.
        await store.InvalidateVerificationTokensAsync(current.Id, "verify_email", ct);
        return new(current with { Email = normalized, EmailVerified = false, EmailKey = newKey }, null);
    }

    /// <summary>Deprovisioning (deactivate) gets its own audit type; every other change is an update.</summary>
    private static string AuditUpdateType(User before, User after) =>
        after.Banned && !before.Banned ? "directory.user_deprovisioned" : "directory.user_updated";

    private static Task AuditAsync(AuthStore store, ScimDirectory directory, string type, Dictionary<string, object> data, CancellationToken ct)
    {
        data["directory_id"] = directory.Id;
        return store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = directory.InstanceId,
            Type = type,
            ActorId = directory.Id,
            Data = data,
            CreatedAt = Now(),
        }, ct);
    }

    private static Task AuditManagementAsync(AuthStore store, InstanceContext context, string type, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = context.Instance.Id,
            Type = type,
            ActorId = context.ApiKeyId,
            Data = data,
            CreatedAt = Now(),
        }, ct);

    /// <summary>Validates member references; null means the payload is invalid.</summary>
    private static async Task<List<string>?> ParseMembersAsync(AuthStore store, ScimDirectory directory, List<ScimMember>? members, CancellationToken ct)
    {
        if (members is null or [])
            return [];
        var ids = new List<string>();
        foreach (var member in members)
        {
            if (string.IsNullOrWhiteSpace(member.Value))
                return null;
            var user = await store.GetUserAsync(member.Value, ct);
            if (user is null || user.InstanceId != directory.InstanceId || user.Deleted)
                return null;
            if (!ids.Contains(member.Value))
                ids.Add(member.Value);
        }
        return ids;
    }

    // ---- SCIM resource views ----------------------------------------------------------

    private static Dictionary<string, object?> UserView(string directoryId, User user)
    {
        var view = new Dictionary<string, object?>
        {
            ["schemas"] = new[] { UserSchema },
            ["id"] = user.Id,
            ["userName"] = user.Email ?? user.Id,
            ["active"] = !user.Banned,
            ["meta"] = new { resourceType = "User", location = $"/scim/{directoryId}/Users/{user.Id}" },
        };
        if (ExternalId(user) is { } externalId)
            view["externalId"] = externalId;
        if (NameView(user.Name) is { } name)
            view["name"] = name;
        return view;
    }

    // The user record carries one free-text Name; SCIM's given/family split rides a
    // first-space split, so a family-only name round-trips as givenName.
    // ponytail: store explicit name parts if lossless round-trip ever matters.
    private static Dictionary<string, object>? NameView(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var separator = name.IndexOf(' ');
        return separator < 0
            ? new Dictionary<string, object> { ["givenName"] = name }
            : new Dictionary<string, object> { ["givenName"] = name[..separator], ["familyName"] = name[(separator + 1)..] };
    }

    private static string? JoinName(ScimName? name)
    {
        if (name is null)
            return null;
        var joined = string.Join(" ", new[] { name.GivenName?.Trim(), name.FamilyName?.Trim() }.Where(part => !string.IsNullOrEmpty(part)));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>The SCIM externalId we park in the user's private metadata; survives document round-trips as JsonElement.</summary>
    private static string? ExternalId(User user) =>
        user.PrivateMetadata.TryGetValue("scimExternalId", out var value)
            ? value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value as string
            : null;

    private static string? NormalizeExternalId(string? externalId) =>
        string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim();

    private static Dictionary<string, object?> GroupView(string directoryId, ScimGroup group) => new()
    {
        ["schemas"] = new[] { GroupSchema },
        ["id"] = group.Id,
        ["displayName"] = group.DisplayName,
        ["members"] = group.MemberUserIds.Select(id => new Dictionary<string, object> { ["value"] = id }).ToList(),
        ["meta"] = new { resourceType = "Group", location = $"/scim/{directoryId}/Groups/{group.Id}" },
    };

    private static object[] SchemaAttributes(params (string Name, string Type, bool ReadOnly)[] attributes) =>
        [.. attributes.Select(a => (object)new
        {
            name = a.Name,
            type = a.Type,
            multiValued = false,
            required = false,
            mutability = a.ReadOnly ? "readOnly" : "readWrite",
            returned = "default",
        })];

    // ---- Request bodies ----------------------------------------------------------------

    /// <summary>RFC 7644 ListResponse — the capitalized "Resources" needs an explicit name;
    /// everything else the default camelCase policy already emits correctly.</summary>
    private sealed record ScimListResponse(
        [property: JsonPropertyName("schemas")] string[] Schemas,
        [property: JsonPropertyName("totalResults")] int TotalResults,
        [property: JsonPropertyName("startIndex")] int StartIndex,
        [property: JsonPropertyName("itemsPerPage")] int ItemsPerPage,
        [property: JsonPropertyName("Resources")] IReadOnlyList<object> Resources);

    private sealed record CreateDirectoryBody([property: JsonPropertyName("org_id")] string? OrgId);

    private sealed record ScimName(string? GivenName, string? FamilyName);

    private sealed record ScimUserBody(string? UserName, ScimName? Name, string? ExternalId, bool? Active);

    private sealed record ScimMember(string? Value, string? Display);

    private sealed record ScimGroupBody(string? DisplayName, List<ScimMember>? Members);

    private sealed record ScimPatchOperation(string? Op, string? Path, JsonElement? Value);

    private sealed record ScimPatchBody(List<ScimPatchOperation>? Operations);
}
