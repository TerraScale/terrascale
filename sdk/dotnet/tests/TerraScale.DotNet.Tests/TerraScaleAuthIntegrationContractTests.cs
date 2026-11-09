using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthIntegrationContractTests
{

    [Test]
    [Arguments("get_account_mfa")]
    [Arguments("set_organization_mfa_policy")]
    [Arguments("create_organization")]
    [Arguments("list_organizations")]
    [Arguments("get_organization")]
    [Arguments("rename_organization")]
    [Arguments("delete_organization")]
    [Arguments("list_members")]
    [Arguments("change_member_role")]
    [Arguments("remove_member")]
    [Arguments("leave_organization")]
    [Arguments("sign_out_everywhere")]
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
    [Arguments("set_project_allowed_cidrs")]
    [Arguments("set_project_api_key_allowed_cidrs")]
    [Arguments("set_organization_api_key_allowed_cidrs")]
    public async Task SourcePinnedPlatformRoutesMatch(string operation)
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
            "get_account_mfa" => client.Platform.GetAccountMfaAsync("fixture-sign-in", cancellationToken),
            "set_organization_mfa_policy" => client.Platform.SetOrganizationMfaPolicyAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformMfaPolicy)!, cancellationToken),
            "set_project_allowed_cidrs" => client.Platform.SetProjectAllowedCidrsAsync("fixture-sign-in", organizationId, projectId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAllowedCidrs)!, cancellationToken),
            "set_project_api_key_allowed_cidrs" => client.Platform.SetProjectApiKeyAllowedCidrsAsync("fixture-sign-in", organizationId, projectId, keyId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAllowedCidrs)!, cancellationToken),
            "set_organization_api_key_allowed_cidrs" => client.Platform.SetOrganizationApiKeyAllowedCidrsAsync("fixture-sign-in", organizationId, keyId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAllowedCidrs)!, cancellationToken),
            "create_organization" => client.Platform.CreateOrganizationAsync("fixture-sign-in", JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName)!, cancellationToken: cancellationToken),
            "list_organizations" => client.Platform.ListOrganizationsAsync("fixture-sign-in", limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "get_organization" => client.Platform.GetOrganizationAsync("fixture-sign-in", organizationId, cancellationToken: cancellationToken),
            "rename_organization" => client.Platform.RenameOrganizationAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName)!, cancellationToken: cancellationToken),
            "delete_organization" => client.Platform.DeleteOrganizationAsync("fixture-sign-in", organizationId, "fixture-step-up", cancellationToken: cancellationToken),
            "list_members" => client.Platform.ListMembersAsync("fixture-sign-in", organizationId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "change_member_role" => client.Platform.ChangeMemberRoleAsync("fixture-sign-in", organizationId, userId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRole)!, cancellationToken: cancellationToken),
            "remove_member" => client.Platform.RemoveMemberAsync("fixture-sign-in", organizationId, userId, cancellationToken: cancellationToken),
            "leave_organization" => client.Platform.LeaveOrganizationAsync("fixture-sign-in", organizationId, cancellationToken: cancellationToken),
            "sign_out_everywhere" => client.Platform.SignOutEverywhereAsync("fixture-sign-in", cancellationToken),
            "delete_account" => client.Platform.DeleteAccountWithProofAsync("fixture-sign-in", "fixture-step-up", cancellationToken: cancellationToken),
            "create_project" => client.Platform.CreateProjectAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName)!, cancellationToken: cancellationToken),
            "list_projects" => client.Platform.ListProjectsAsync("fixture-sign-in", organizationId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "get_project" => client.Platform.GetProjectAsync("fixture-sign-in", organizationId, projectId, cancellationToken: cancellationToken),
            "rename_project" => client.Platform.RenameProjectAsync("fixture-sign-in", organizationId, projectId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName)!, cancellationToken: cancellationToken),
            "delete_project" => client.Platform.DeleteProjectAsync("fixture-sign-in", organizationId, projectId, cancellationToken: cancellationToken),
            "list_project_members" => client.Platform.ListProjectMembersAsync("fixture-sign-in", organizationId, projectId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "set_project_member_role" => client.Platform.SetProjectMemberRoleAsync("fixture-sign-in", organizationId, projectId, userId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRole)!, cancellationToken: cancellationToken),
            "remove_project_member" => client.Platform.RemoveProjectMemberAsync("fixture-sign-in", organizationId, projectId, userId, cancellationToken: cancellationToken),
            "create_organization_api_key" => client.Platform.CreateOrganizationApiKeyAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateApiKey)!, "fixture-step-up", cancellationToken: cancellationToken),
            "list_organization_api_keys" => client.Platform.ListOrganizationApiKeysAsync("fixture-sign-in", organizationId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "get_organization_api_key" => client.Platform.GetOrganizationApiKeyAsync("fixture-sign-in", organizationId, keyId, cancellationToken: cancellationToken),
            "revoke_organization_api_key" => client.Platform.RevokeOrganizationApiKeyAsync("fixture-sign-in", organizationId, keyId, cancellationToken: cancellationToken),
            "rotate_organization_api_key" => client.Platform.RotateOrganizationApiKeyAsync("fixture-sign-in", organizationId, keyId, "fixture-step-up", JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRotateApiKey)!, cancellationToken: cancellationToken),
            "create_project_api_key" => client.Platform.CreateProjectApiKeyAsync("fixture-sign-in", organizationId, projectId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateApiKey)!, "fixture-step-up", cancellationToken: cancellationToken),
            "list_project_api_keys" => client.Platform.ListProjectApiKeysAsync("fixture-sign-in", organizationId, projectId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "get_project_api_key" => client.Platform.GetProjectApiKeyAsync("fixture-sign-in", organizationId, projectId, keyId, cancellationToken: cancellationToken),
            "revoke_project_api_key" => client.Platform.RevokeProjectApiKeyAsync("fixture-sign-in", organizationId, projectId, keyId, cancellationToken: cancellationToken),
            "rotate_project_api_key" => client.Platform.RotateProjectApiKeyAsync("fixture-sign-in", organizationId, projectId, keyId, "fixture-step-up", JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRotateApiKey)!, cancellationToken: cancellationToken),
            "create_invitation" => client.Platform.CreateInvitationAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateInvitation)!, cancellationToken: cancellationToken),
            "list_invitations" => client.Platform.ListInvitationsAsync("fixture-sign-in", organizationId, limit: 2, after: "opaque+/cursor=", cancellationToken: cancellationToken),
            "revoke_invitation" => client.Platform.RevokeInvitationAsync("fixture-sign-in", organizationId, invitationId, cancellationToken: cancellationToken),
            "lookup_invitation" => client.Platform.LookupInvitationAsync("fixture-sign-in", JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationToken)!, cancellationToken: cancellationToken),
            "accept_invitation" => client.Platform.AcceptInvitationAsync("fixture-sign-in", JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationToken)!, cancellationToken: cancellationToken),
            "decline_invitation" => client.Platform.DeclineInvitationAsync("fixture-sign-in", JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationToken)!, cancellationToken: cancellationToken),
            "start_ownership_transfer" => client.Platform.StartOwnershipTransferAsync("fixture-sign-in", organizationId, JsonSerializer.Deserialize(requestJson!, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformStartOwnershipTransfer)!, "fixture-step-up", cancellationToken: cancellationToken),
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
        await Assert.That(handler.Authorization!.ToString()).IsEqualTo("Bearer fixture-sign-in");
        var needsProof = contract.TryGetProperty("step_up", out _);
        await Assert.That(handler.StepUpProof).IsEqualTo(needsProof ? "fixture-step-up" : null);
        await Assert.That(http.DefaultRequestHeaders.Contains("X-TerraScale-Step-Up-Proof")).IsFalse();
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
            ? (await platform.DeleteAccountWithProofAsync("fixture-sign-in", "fixture-step-up")).Completed
            : await platform.DeleteProjectAsync("fixture-sign-in", "01JA0000000000000000000005", "01JA0000000000000000000006");
        await Assert.That(answer).IsEqualTo(done);
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    // Any other success status is not evidence of either outcome.

    // Every refusal ts-auth answers for a closure keeps its code (admitted vocabulary), sent once.

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
            if (operation.TryGetProperty("deletion_response_dto", out var deletionDto))
                await AssertFixtureShapeAsync(fixtures.RootElement.GetProperty("consume_step_up_delete").GetProperty("response"), dtos, deletionDto.GetString()!);
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

    private const string CidrId = "01JA0000000000000000000001";
    private static Task SetCidrs(TerraScaleAuthClient client, string target, TerraScaleAuthPlatformAllowedCidrs request, CancellationToken token = default) => target switch
    {
        "project" => client.Platform.SetProjectAllowedCidrsAsync("fixture-sign-in", CidrId, CidrId, request, token),
        "project_api_key" => client.Platform.SetProjectApiKeyAllowedCidrsAsync("fixture-sign-in", CidrId, CidrId, CidrId, request, token),
        _ => client.Platform.SetOrganizationApiKeyAllowedCidrsAsync("fixture-sign-in", CidrId, CidrId, request, token)
    };

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
            else if (type == "bool") await Assert.That(value.ValueKind is JsonValueKind.True or JsonValueKind.False).IsTrue();
            else await Assert.That(value.ValueKind).IsEqualTo(type is "int" or "long" ? JsonValueKind.Number : JsonValueKind.String);
        }
    }

    private static async Task<string?> ResultJsonAsync(Task call) => call switch
    {
        Task<TerraScaleAuthPlatformAccountMfa> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAccountMfa),
        Task<TerraScaleAuthPlatformOrganization> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOrganization),
        Task<TerraScaleAuthPlatformMember> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformMember),
        Task<TerraScaleAuthPlatformProject> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProject),
        Task<TerraScaleAuthPlatformProjectMember> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProjectMember),
        Task<TerraScaleAuthPlatformApiKey> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKey),
        Task<TerraScaleAuthPlatformApiKeySecret> typed => JsonSerializer.Serialize(await typed, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret),
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
    });

    private sealed class CaptureHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Method { get; private set; }
        public string? Body { get; private set; }
        public string? MediaType { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? StepUpProof { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            StepUpProof = request.Headers.TryGetValues("X-TerraScale-Step-Up-Proof", out var proofs) ? proofs.Single() : null;
            Calls++; Uri = request.RequestUri; Method = request.Method.Method; Authorization = request.Headers.Authorization;
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
