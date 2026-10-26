using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthIntegrationContractTests
{
    private const string ClientId = "fixture id:é+";
    private const string ClientSecret = "fixture secret:+=/é";

    [Test]
    [Arguments("create_organization")]
    [Arguments("list_organizations")]
    [Arguments("get_organization")]
    [Arguments("rename_organization")]
    [Arguments("delete_organization")]
    [Arguments("list_members")]
    [Arguments("change_member_role")]
    [Arguments("remove_member")]
    [Arguments("leave_organization")]
    [Arguments("delete_account")]
    [Arguments("create_project")]
    [Arguments("list_projects")]
    [Arguments("get_project")]
    [Arguments("rename_project")]
    [Arguments("delete_project")]
    [Arguments("list_project_members")]
    [Arguments("set_project_member_role")]
    [Arguments("remove_project_member")]
    [Arguments("create_organization_api_key")]
    [Arguments("list_organization_api_keys")]
    [Arguments("get_organization_api_key")]
    [Arguments("revoke_organization_api_key")]
    [Arguments("rotate_organization_api_key")]
    [Arguments("create_project_api_key")]
    [Arguments("list_project_api_keys")]
    [Arguments("get_project_api_key")]
    [Arguments("revoke_project_api_key")]
    [Arguments("rotate_project_api_key")]
    [Arguments("create_invitation")]
    [Arguments("list_invitations")]
    [Arguments("revoke_invitation")]
    [Arguments("lookup_invitation")]
    [Arguments("accept_invitation")]
    [Arguments("decline_invitation")]
    [Arguments("start_ownership_transfer")]
    [Arguments("get_ownership_transfer")]
    [Arguments("cancel_ownership_transfer")]
    [Arguments("confirm_ownership_transfer")]
    [Arguments("list_audit_events")]
    public async Task SourcePinnedPlatformAndHeadlessRoutesMatch(string operation)
    {
        var ids = new TerraScaleIdGenerator();
        var organizationId = ids.NewId(); var projectId = ids.NewId(); var userId = ids.NewId(); var keyId = ids.NewId();
        var invitationId = ids.NewId(); var nomineeId = ids.NewId(); var eventId = ids.NewId();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath("routes.json")));
        var contract = manifest.RootElement.GetProperty("operations").EnumerateArray().Single(item => item.GetProperty("operation_id").GetString() == operation);
        var fixtureJson = await File.ReadAllTextAsync(ContractPath("fixtures/platform-headless.json"));
        fixtureJson = fixtureJson.Replace("{organization}", organizationId, StringComparison.Ordinal).Replace("{project}", projectId, StringComparison.Ordinal)
            .Replace("{user}", userId, StringComparison.Ordinal).Replace("{key}", keyId, StringComparison.Ordinal)
            .Replace("{invitation}", invitationId, StringComparison.Ordinal).Replace("{nominee}", nomineeId, StringComparison.Ordinal).Replace("{event}", eventId, StringComparison.Ordinal);
        using var fixtures = JsonDocument.Parse(fixtureJson);
        var fixture = fixtures.RootElement.GetProperty(operation);
        var requestJson = fixture.GetProperty("request").ValueKind == JsonValueKind.Null ? null : fixture.GetProperty("request").GetRawText();
        var responseJson = fixture.GetProperty("response").ValueKind == JsonValueKind.Null ? "" : fixture.GetProperty("response").GetRawText();
        using var handler = new CaptureHandler((HttpStatusCode)contract.GetProperty("success_status").GetInt32(), responseJson);
        using var http = new HttpClient(handler);
        var client = Client(http);
        var cancellationToken = CancellationToken.None;
        Task call = operation switch
        {
            "create_organization" => client.Platform.CreateOrganizationAsync("fixture-sign-in", JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName)!, cancellationToken: cancellationToken),
            "list_organizations" => client.Platform.ListOrganizationsAsync("fixture-sign-in", limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "get_organization" => client.Platform.GetOrganizationAsync("fixture-sign-in", organizationId, cancellationToken: cancellationToken),
            "rename_organization" => client.Platform.RenameOrganizationAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName)!, cancellationToken: cancellationToken),
            "delete_organization" => client.Platform.DeleteOrganizationAsync("fixture-sign-in", organizationId, cancellationToken: cancellationToken),
            "list_members" => client.Platform.ListMembersAsync("fixture-sign-in", organizationId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "change_member_role" => client.Platform.ChangeMemberRoleAsync("fixture-sign-in", organizationId, userId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRole)!, cancellationToken: cancellationToken),
            "remove_member" => client.Platform.RemoveMemberAsync("fixture-sign-in", organizationId, userId, cancellationToken: cancellationToken),
            "leave_organization" => client.Platform.LeaveOrganizationAsync("fixture-sign-in", organizationId, cancellationToken: cancellationToken),
            "delete_account" => client.Platform.DeleteAccountAsync("fixture-sign-in", cancellationToken: cancellationToken),
            "create_project" => client.Platform.CreateProjectAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName)!, cancellationToken: cancellationToken),
            "list_projects" => client.Platform.ListProjectsAsync("fixture-sign-in", organizationId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "get_project" => client.Platform.GetProjectAsync("fixture-sign-in", organizationId, projectId, cancellationToken: cancellationToken),
            "rename_project" => client.Platform.RenameProjectAsync("fixture-sign-in", organizationId, projectId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName)!, cancellationToken: cancellationToken),
            "delete_project" => client.Platform.DeleteProjectAsync("fixture-sign-in", organizationId, projectId, cancellationToken: cancellationToken),
            "list_project_members" => client.Platform.ListProjectMembersAsync("fixture-sign-in", organizationId, projectId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "set_project_member_role" => client.Platform.SetProjectMemberRoleAsync("fixture-sign-in", organizationId, projectId, userId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRole)!, cancellationToken: cancellationToken),
            "remove_project_member" => client.Platform.RemoveProjectMemberAsync("fixture-sign-in", organizationId, projectId, userId, cancellationToken: cancellationToken),
            "create_organization_api_key" => client.Platform.CreateOrganizationApiKeyAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateApiKey)!, cancellationToken: cancellationToken),
            "list_organization_api_keys" => client.Platform.ListOrganizationApiKeysAsync("fixture-sign-in", organizationId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "get_organization_api_key" => client.Platform.GetOrganizationApiKeyAsync("fixture-sign-in", organizationId, keyId, cancellationToken: cancellationToken),
            "revoke_organization_api_key" => client.Platform.RevokeOrganizationApiKeyAsync("fixture-sign-in", organizationId, keyId, cancellationToken: cancellationToken),
            "rotate_organization_api_key" => client.Platform.RotateOrganizationApiKeyAsync("fixture-sign-in", organizationId, keyId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRotateApiKey)!, cancellationToken: cancellationToken),
            "create_project_api_key" => client.Platform.CreateProjectApiKeyAsync("fixture-sign-in", organizationId, projectId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateApiKey)!, cancellationToken: cancellationToken),
            "list_project_api_keys" => client.Platform.ListProjectApiKeysAsync("fixture-sign-in", organizationId, projectId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "get_project_api_key" => client.Platform.GetProjectApiKeyAsync("fixture-sign-in", organizationId, projectId, keyId, cancellationToken: cancellationToken),
            "revoke_project_api_key" => client.Platform.RevokeProjectApiKeyAsync("fixture-sign-in", organizationId, projectId, keyId, cancellationToken: cancellationToken),
            "rotate_project_api_key" => client.Platform.RotateProjectApiKeyAsync("fixture-sign-in", organizationId, projectId, keyId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRotateApiKey)!, cancellationToken: cancellationToken),
            "headless_email_code_start" => client.Headless.StartEmailCodeAsync(ClientId, ClientSecret, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessEmailCode)!, cancellationToken: cancellationToken),
            "headless_sign_up" => client.Headless.SignUpAsync(ClientId, ClientSecret, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessSignUp)!, cancellationToken: cancellationToken),
            "headless_password_reset_start" => client.Headless.StartPasswordResetAsync(ClientId, ClientSecret, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessEmail)!, cancellationToken: cancellationToken),
            "headless_password_reset_complete" => client.Headless.CompletePasswordResetAsync(ClientId, ClientSecret, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessPasswordReset)!, cancellationToken: cancellationToken),
            "create_invitation" => client.Platform.CreateInvitationAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateInvitation)!, cancellationToken: cancellationToken),
            "list_invitations" => client.Platform.ListInvitationsAsync("fixture-sign-in", organizationId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "revoke_invitation" => client.Platform.RevokeInvitationAsync("fixture-sign-in", organizationId, invitationId, cancellationToken: cancellationToken),
            "lookup_invitation" => client.Platform.LookupInvitationAsync("fixture-sign-in", JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationToken)!, cancellationToken: cancellationToken),
            "accept_invitation" => client.Platform.AcceptInvitationAsync("fixture-sign-in", JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationToken)!, cancellationToken: cancellationToken),
            "decline_invitation" => client.Platform.DeclineInvitationAsync("fixture-sign-in", JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationToken)!, cancellationToken: cancellationToken),
            "start_ownership_transfer" => client.Platform.StartOwnershipTransferAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformStartOwnershipTransfer)!, cancellationToken: cancellationToken),
            "get_ownership_transfer" => client.Platform.GetOwnershipTransferAsync("fixture-sign-in", organizationId, cancellationToken: cancellationToken),
            "cancel_ownership_transfer" => client.Platform.CancelOwnershipTransferAsync("fixture-sign-in", organizationId, cancellationToken: cancellationToken),
            "confirm_ownership_transfer" => client.Platform.ConfirmOwnershipTransferAsync("fixture-sign-in", organizationId, cancellationToken: cancellationToken),
            "list_audit_events" => client.Platform.ListAuditEventsAsync("fixture-sign-in", organizationId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            _ => throw new ArgumentException("Unknown contract operation.", nameof(operation))
        };
        await call;
        var resultJson = await ResultJsonAsync(call);
        if (resultJson is not null)
            await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(resultJson), JsonNode.Parse(responseJson))).IsTrue();
        var path = contract.GetProperty("path").GetString()!.Replace("{slug}", "terrascale", StringComparison.Ordinal)
            .Replace("{organization}", organizationId, StringComparison.Ordinal).Replace("{project}", projectId, StringComparison.Ordinal)
            .Replace("{user}", userId, StringComparison.Ordinal).Replace("{key}", keyId, StringComparison.Ordinal)
            .Replace("{invitation}", invitationId, StringComparison.Ordinal).Replace("{nominee}", nomineeId, StringComparison.Ordinal).Replace("{event}", eventId, StringComparison.Ordinal);
        if (contract.GetProperty("pagination").ValueKind != JsonValueKind.Null) path += "?limit=2&after=opaque%2B%2Fcursor%3D";
        await Assert.That(handler.Uri!.PathAndQuery).IsEqualTo(path);
        await Assert.That(handler.Method).IsEqualTo(contract.GetProperty("method").GetString());
        await Assert.That(handler.Uri.Host).IsEqualTo("auth.example.test");
        await Assert.That(handler.Calls).IsEqualTo(1);
        if (operation.StartsWith("headless_", StringComparison.Ordinal))
        {
            await Assert.That(handler.Authorization!.Scheme).IsEqualTo("Basic");
            var credential = Encoding.ASCII.GetString(Convert.FromBase64String(handler.Authorization.Parameter!)).Split(':');
            await Assert.That(Uri.UnescapeDataString(credential[0].Replace("+", " ", StringComparison.Ordinal))).IsEqualTo(ClientId);
            await Assert.That(Uri.UnescapeDataString(credential[1].Replace("+", " ", StringComparison.Ordinal))).IsEqualTo(ClientSecret);
        }
        else await Assert.That(handler.Authorization!.ToString()).IsEqualTo("Bearer fixture-sign-in");
        if (requestJson is not null)
        {
            await Assert.That(handler.MediaType).IsEqualTo("application/json");
            await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(handler.Body!), JsonNode.Parse(requestJson))).IsTrue();
        }
        else await Assert.That(handler.Body).IsNull();
    }

    // ts-auth answers 204 once a deletion is done and 202 while a project closure is pending (routes.json pending_status).
    [Test]
    [Arguments("delete_account", 204, true)]
    [Arguments("delete_account", 202, false)]
    [Arguments("delete_project", 204, true)]
    [Arguments("delete_project", 202, false)]
    public async Task Deletions_report_done_or_pending(string operation, int status, bool done)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath("routes.json")));
        var contract = manifest.RootElement.GetProperty("operations").EnumerateArray().Single(item => item.GetProperty("operation_id").GetString() == operation);
        await Assert.That(contract.GetProperty(done ? "success_status" : "pending_status").GetInt32()).IsEqualTo(status);
        using var handler = new CaptureHandler((HttpStatusCode)status, "");
        using var http = new HttpClient(handler);
        var platform = Client(http).Platform;
        var answer = operation == "delete_account"
            ? await platform.DeleteAccountAsync("fixture-sign-in")
            : await platform.DeleteProjectAsync("fixture-sign-in", "01JA0000000000000000000005", "01JA0000000000000000000006");
        await Assert.That(answer).IsEqualTo(done);
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    // Any other success status is not evidence of either outcome.
    [Test]
    [Arguments(200)]
    [Arguments(201)]
    public async Task Deletions_refuse_other_success_statuses(int status)
    {
        using var handler = new CaptureHandler((HttpStatusCode)status, "");
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Platform.DeleteProjectAsync("fixture-sign-in", "01JA0000000000000000000005", "01JA0000000000000000000006"));
        await Assert.That(exception!.ErrorCode).IsEqualTo("invalid_response");
    }

    // Every refusal ts-auth answers for a closure keeps its code (admitted vocabulary), sent once.
    [Test]
    [Arguments("delete_project", "not_empty")]
    [Arguments("delete_project", "closure_conflict")]
    [Arguments("delete_project", "binding_conflict")]
    [Arguments("delete_account", "last_owner")]
    [Arguments("delete_account", "not_empty")]
    [Arguments("delete_account", "closure_conflict")]
    [Arguments("delete_account", "binding_conflict")]
    public async Task Deletions_surface_each_closure_refusal_once(string operation, string code)
    {
        using var handler = new CaptureHandler(HttpStatusCode.Conflict, $$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""");
        using var http = new HttpClient(handler);
        var platform = Client(http).Platform;
        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => operation == "delete_account"
            ? platform.DeleteAccountAsync("fixture-sign-in")
            : platform.DeleteProjectAsync("fixture-sign-in", "01JA0000000000000000000005", "01JA0000000000000000000006"));
        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(exception.ErrorCode).IsEqualTo(code);
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task DeleteAccountSurfacesLastOwnerConflictOnce()
    {
        using var handler = new CaptureHandler(HttpStatusCode.Conflict, """{"error":{"code":"last_owner","message":"transfer ownership first"}}""");
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Platform.DeleteAccountAsync("fixture-sign-in"));
        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(exception.ErrorCode).IsEqualTo("last_owner");
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExportedFixtureShapesMatchDtoManifestRecursively()
    {
        using var schemas = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath("dtos.json")));
        using var routes = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath("routes.json")));
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath("fixtures/platform-headless.json")));
        var dtos = schemas.RootElement.GetProperty("dtos");
        foreach (var operation in routes.RootElement.GetProperty("operations").EnumerateArray())
        {
            var fixture = fixtures.RootElement.GetProperty(operation.GetProperty("operation_id").GetString()!);
            foreach (var kind in new[] { "request", "response" })
            {
                var dto = operation.GetProperty(kind + "_dto");
                if (dto.ValueKind != JsonValueKind.Null) await AssertFixtureShapeAsync(fixture.GetProperty(kind), dtos, dto.GetString()!);
                else await Assert.That(fixture.GetProperty(kind).ValueKind).IsEqualTo(JsonValueKind.Null);
            }
        }
        using var oauth = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath("fixtures/oauth.json")));
        await AssertFixtureShapeAsync(oauth.RootElement.GetProperty("nested_error"), dtos, "ErrorBody");
        await AssertFixtureShapeAsync(oauth.RootElement.GetProperty("oauth_error"), dtos, "OAuthErrorBody");
    }

    [Test]
    public async Task SecurityErrorListsStayInsideAdmittedVocabulary()
    {
        using var security = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath("security.json")));
        using var vocabulary = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath("error-codes.json")));
        var codes = vocabulary.RootElement.GetProperty("codes").EnumerateArray().Select(code => code.GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var list in new[] { "platform_codes", "token_codes" })
            foreach (var code in security.RootElement.GetProperty("errors").GetProperty(list).EnumerateArray())
                await Assert.That(codes.Contains(code.GetString()!)).IsTrue();
    }

    [Test]
    public async Task FixturesRespectProducerRolesKeyScopeAndRotationClock()
    {
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(ContractPath("fixtures/platform-headless.json")));
        var root = fixtures.RootElement;
        // PlatformEndpoints.Map(..., Roles.Owner/Admin); rename authority is Roles.Admin.
        await Assert.That(root.GetProperty("create_organization").GetProperty("response").GetProperty("role").GetString()).IsEqualTo("owner");
        await Assert.That(root.GetProperty("create_project").GetProperty("response").GetProperty("role").GetString()).IsEqualTo("admin");
        foreach (var operation in new[] { "rename_organization", "rename_project" })
            await Assert.That(root.GetProperty(operation).GetProperty("response").GetProperty("role").GetString()).IsEqualTo("admin");
        // ApiKeyEndpoint.ProjectId() is null and its creation guard refuses database for organization keys.
        await Assert.That(root.GetProperty("create_organization_api_key").GetProperty("request").GetProperty("database").ValueKind).IsEqualTo(JsonValueKind.Null);
        foreach (var action in new[] { "create", "list", "get", "rotate" })
        {
            var response = root.GetProperty(action + "_organization_api_key" + (action == "list" ? "s" : "")).GetProperty("response");
            var keys = action == "list" ? response.GetProperty("items").EnumerateArray().ToArray()
                : new[] { action == "get" ? response : response.GetProperty("api_key") };
            foreach (var key in keys)
            {
                await Assert.That(key.GetProperty("project_id").ValueKind).IsEqualTo(JsonValueKind.Null);
                await Assert.That(key.GetProperty("database").ValueKind).IsEqualTo(JsonValueKind.Null);
            }
        }
        var now = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        foreach (var scope in new[] { "organization", "project" })
        {
            var rotation = root.GetProperty("rotate_" + scope + "_api_key");
            // ApiKeys.RotateAsync sets now + previousTtl; ApiKeyEndpoint.Map exposes active grace only.
            var ttl = rotation.GetProperty("request").GetProperty("previous_secret_ttl").GetInt32();
            await Assert.That(rotation.GetProperty("response").GetProperty("api_key").GetProperty("previous_secret_expires_at").GetDateTimeOffset()).IsEqualTo(now.AddSeconds(ttl));
        }
        var invitation = root.GetProperty("create_invitation").GetProperty("response").GetProperty("invitation");
        await Assert.That(invitation.GetProperty("status").GetString()).IsEqualTo("pending");
        await Assert.That(invitation.GetProperty("expires_at").GetDateTimeOffset()).IsEqualTo(now.AddDays(7));
        var transfer = root.GetProperty("start_ownership_transfer").GetProperty("response");
        await Assert.That(transfer.GetProperty("expires_at").GetDateTimeOffset()).IsEqualTo(now.AddDays(7));
        await Assert.That(transfer.GetProperty("from_user_id").GetString() == transfer.GetProperty("to_user_id").GetString()).IsFalse();
        await Assert.That(root.GetProperty("confirm_ownership_transfer").GetProperty("response").GetProperty("role").GetString()).IsEqualTo("owner");
    }

    [Test]
    public async Task ProjectInvitationFilterUsesCanonicalQueryWithoutRestartingPagination()
    {
        var ids = new TerraScaleIdGenerator(); var organization = ids.NewId(); var project = ids.NewId();
        using var handler = new CaptureHandler(HttpStatusCode.OK, "{\"items\":[],\"next\":null}");
        using var http = new HttpClient(handler);
        await Client(http).Platform.ListInvitationsAsync("fixture-sign-in", organization, after: "opaque+/cursor=", projectId: project);
        await Assert.That(handler.Uri!.PathAndQuery).IsEqualTo($"/v1/terrascale/organizations/{organization}/invitations?after=opaque%2B%2Fcursor%3D&project_id={project}");
        await Assert.ThrowsAsync<ArgumentException>(() => Client(http).Platform.ListInvitationsAsync("fixture-sign-in", organization, projectId: "not-an-id"));
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("192.0.2.1")]
    [Arguments("2001:db8::1")]
    public async Task TypedHeadlessIpAddressesRemainStringWireValues(string address)
    {
        using var handler = new CaptureHandler(HttpStatusCode.Accepted, "");
        using var http = new HttpClient(handler);
        await Client(http).Headless.StartEmailCodeAsync(ClientId, ClientSecret, new("synthetic@example.test", IPAddress.Parse(address)));
        using var body = JsonDocument.Parse(handler.Body!);
        await Assert.That(body.RootElement.GetProperty("ip_address").GetString()).IsEqualTo(address);
        var decoded = JsonSerializer.Deserialize(handler.Body!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessEmailCode)!;
        await Assert.That(decoded.IpAddress).IsEqualTo(IPAddress.Parse(address));
    }

    [Test]
    public async Task InvitationTokensRedactFormatting()
    {
        const string token = "fixture-invitation-canary";
        var request = new TerraScaleAuthPlatformInvitationToken(token);
        var created = new TerraScaleAuthPlatformCreatedInvitation(new("fixture-id", "synthetic@example.test", "member", null,
            "fixture-user", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(7), "pending"), token);
        await Assert.That(request.ToString()).DoesNotContain(token);
        await Assert.That(created.ToString()).DoesNotContain(token);
    }

    private static async Task AssertFixtureShapeAsync(JsonElement fixture, JsonElement dtos, string dtoName)
    {
        var fields = dtos.GetProperty(dtoName).GetProperty("fields").EnumerateArray().ToArray();
        await Assert.That(fixture.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(fixture.EnumerateObject().Select(field => field.Name).ToArray())
            .IsEquivalentTo(fields.Select(field => field.GetProperty("name").GetString()!).ToArray());
        foreach (var field in fields)
        {
            var value = fixture.GetProperty(field.GetProperty("name").GetString()!);
            if (value.ValueKind == JsonValueKind.Null)
            {
                await Assert.That(field.GetProperty("nullable").GetBoolean()).IsTrue();
                continue;
            }
            var type = field.GetProperty("type").GetString()!.TrimEnd('?');
            if (type.StartsWith("IReadOnlyList<", StringComparison.Ordinal))
            {
                await Assert.That(value.ValueKind).IsEqualTo(JsonValueKind.Array);
                var itemType = type[14..^1];
                if (dtos.TryGetProperty(itemType, out _))
                    foreach (var item in value.EnumerateArray()) await AssertFixtureShapeAsync(item, dtos, itemType);
            }
            else if (dtos.TryGetProperty(type, out _)) await AssertFixtureShapeAsync(value, dtos, type);
            else await Assert.That(value.ValueKind).IsEqualTo(type is "int" or "long" ? JsonValueKind.Number : JsonValueKind.String);
        }
    }

    private static async Task<string?> ResultJsonAsync(Task call) => call switch
    {
        Task<TerraScaleAuthPlatformOrganization> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOrganization),
        Task<TerraScaleAuthPlatformMember> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformMember),
        Task<TerraScaleAuthPlatformProject> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProject),
        Task<TerraScaleAuthPlatformProjectMember> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProjectMember),
        Task<TerraScaleAuthPlatformApiKey> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKey),
        Task<TerraScaleAuthPlatformApiKeySecret> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret),
        Task<TerraScaleAuthHeadlessPendingSignUp> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessPendingSignUp),
        Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformOrganization>> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformOrganization),
        Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformMember>> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformMember),
        Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProject>> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformProject),
        Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProjectMember>> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformProjectMember),
        Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformApiKey>> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformApiKey),
        Task<TerraScaleAuthPlatformCreatedInvitation> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreatedInvitation),
        Task<TerraScaleAuthPlatformInvitationLookup> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationLookup),
        Task<TerraScaleAuthPlatformAcceptedInvitation> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAcceptedInvitation),
        Task<TerraScaleAuthPlatformOwnershipTransfer> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOwnershipTransfer),
        Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformInvitation>> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformInvitation),
        Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformAuditEvent>> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformAuditEvent),
        _ => null
    };

    private static string ContractPath(string relative, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../contracts/auth", relative));

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.example.test"),
        InstanceSlug = "terrascale",
        MaxRetryAttempts = 2,
        RetryDelay = TimeSpan.Zero
    });

    private sealed class CaptureHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Method { get; private set; }
        public string? Body { get; private set; }
        public string? MediaType { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Uri = request.RequestUri; Method = request.Method.Method; Authorization = request.Headers.Authorization;
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
