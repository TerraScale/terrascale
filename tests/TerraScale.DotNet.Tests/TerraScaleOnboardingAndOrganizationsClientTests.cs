using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleOnboardingAndOrganizationsClientTests
{
    [Test]
    public async Task OnboardingOperations_SendEveryCanonicalRouteAndDeserializeState()
    {
        using var handler = new ContractHandler();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var initial = await client.Onboarding.GetStateAsync();
        var organization = await client.Onboarding.CreateOrganizationAsync(
            new TerraScaleCreateOnboardingOrganizationRequest("Analytical Engines"));
        var completed = await client.Onboarding.CreateProjectAsync(
            new TerraScaleCreateOnboardingProjectRequest(
                "Primary",
                [TerraScaleRegions.Gru, TerraScaleRegions.Fra],
                TerraScaleRegions.Gru));

        await Assert.That(initial.UserId).IsEqualTo("user-a");
        await Assert.That(organization.OrganizationId).IsEqualTo("organization-a");
        await Assert.That(completed.ProjectPrimaryRegions)
            .IsEquivalentTo([TerraScaleRegions.Gru, TerraScaleRegions.Fra]);
        await Assert.That(handler.Requests.Select(static request => (request.Method, request.PathAndQuery)))
            .IsEquivalentTo(
            [
                ("GET", "/api/v1/onboarding"),
                ("POST", "/api/v1/onboarding/organization"),
                ("POST", "/api/v1/onboarding/project")
            ]);

        using var organizationBody = JsonDocument.Parse(handler.Requests[1].Body!);
        await Assert.That(organizationBody.RootElement.GetProperty("name").GetString())
            .IsEqualTo("Analytical Engines");
        using var projectBody = JsonDocument.Parse(handler.Requests[2].Body!);
        await Assert.That(projectBody.RootElement.GetProperty("regions").EnumerateArray()
                .Select(static region => region.GetString()
                    ?? throw new InvalidDataException("A serialized onboarding region is null."))
                .ToArray())
            .IsEquivalentTo([TerraScaleRegions.Gru, TerraScaleRegions.Fra]);
        await Assert.That(projectBody.RootElement.GetProperty("backupRegion").GetString())
            .IsEqualTo(TerraScaleRegions.Gru);
    }

    [Test]
    public async Task OrganizationOperations_SendEveryCanonicalRouteAndTypedBody()
    {
        using var handler = new ContractHandler();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        _ = await client.Organizations.ListAsync(skip: 2, take: 25);
        _ = await client.Organizations.CreateAsync(new TerraScaleCreateOrganizationRequest("Analytical Engines"));
        _ = await client.Organizations.GetAsync("organization-a");
        _ = await client.Organizations.UpdateAsync(
            "organization-a",
            new TerraScaleUpdateOrganizationRequest("Analytical Engines Two"));
        _ = await client.Organizations.RequestDeletionAsync(
            "organization-a",
            new TerraScaleRequestOrganizationDeletionRequest(true, "Analytical Engines Two"));
        await client.Organizations.LeaveAsync("organization-a");
        _ = await client.Organizations.ListMembersAsync("organization-a", skip: 3, take: 20);
        _ = await client.Organizations.UpdateMemberRoleAsync(
            "organization-a",
            "user-a",
            new TerraScaleUpdateOrganizationMemberRoleRequest(TerraScaleOrganizationMemberRoles.Owner));
        await client.Organizations.RemoveMemberAsync("organization-a", "user-a");
        _ = await client.Organizations.ListInvitationsAsync("organization-a", skip: 4, take: 15);
        _ = await client.Organizations.CreateInvitationAsync(
            "organization-a",
            new TerraScaleCreateOrganizationInvitationRequest(
                "grace@example.test",
                "https://app.example.test/invitations/{invitationId}?organizationId={organizationId}",
                "project-a"));
        _ = await client.Organizations.AcceptInvitationAsync("organization-a", "invitation-a");
        _ = await client.Organizations.DeclineInvitationAsync("organization-a", "invitation-a");
        _ = await client.Organizations.ResendInvitationAsync("organization-a", "invitation-a");
        _ = await client.Organizations.CancelInvitationAsync("organization-a", "invitation-a");

        await Assert.That(handler.Requests.Select(static request => (request.Method, request.PathAndQuery)))
            .IsEquivalentTo(
            [
                ("GET", "/api/v1/organizations?skip=2&take=25"),
                ("POST", "/api/v1/organizations"),
                ("GET", "/api/v1/organizations/organization-a"),
                ("PATCH", "/api/v1/organizations/organization-a"),
                ("POST", "/api/v1/organizations/organization-a:request-deletion"),
                ("POST", "/api/v1/organizations/organization-a:leave"),
                ("GET", "/api/v1/organizations/organization-a/members?skip=3&take=20"),
                ("PATCH", "/api/v1/organizations/organization-a/members/user-a"),
                ("POST", "/api/v1/organizations/organization-a/members/user-a:remove"),
                ("GET", "/api/v1/organizations/organization-a/invitations?skip=4&take=15"),
                ("POST", "/api/v1/organizations/organization-a/invitations"),
                ("POST", "/api/v1/organizations/organization-a/invitations/invitation-a:accept"),
                ("POST", "/api/v1/organizations/organization-a/invitations/invitation-a:decline"),
                ("POST", "/api/v1/organizations/organization-a/invitations/invitation-a:resend"),
                ("POST", "/api/v1/organizations/organization-a/invitations/invitation-a:cancel")
            ]);
        await Assert.That(handler.Requests.All(static request => request.Authorization == "Bearer caller-token"))
            .IsTrue();

        using var removalBody = JsonDocument.Parse(handler.Requests[8].Body!);
        await Assert.That(removalBody.RootElement.GetProperty("confirm").GetBoolean()).IsTrue();
        using var invitationBody = JsonDocument.Parse(handler.Requests[10].Body!);
        await Assert.That(invitationBody.RootElement.GetProperty("email").GetString())
            .IsEqualTo("grace@example.test");
        await Assert.That(invitationBody.RootElement.GetProperty("projectId").GetString())
            .IsEqualTo("project-a");
    }

    [Test]
    public async Task RegionBearingOnboardingRequests_RejectNonCanonicalCodesBeforeSending()
    {
        string[] rejectedCodes = ["gru", " GRU", "GRU ", "SJC", "GR", "GRUU", "ĞRU"];
        foreach (var rejectedCode in rejectedCodes)
        {
            using var handler = new ContractHandler();
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(httpClient);

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                client.Onboarding.CreateProjectAsync(
                    new TerraScaleCreateOnboardingProjectRequest(
                        "Primary",
                        [rejectedCode],
                        TerraScaleRegions.Gru)));

            await Assert.That(exception).IsNotNull();
            await Assert.That(handler.Requests).IsEmpty();
        }
    }

    [Test]
    public async Task OrganizationMutations_RejectUnsafeInputsBeforeSending()
    {
        using var handler = new ContractHandler();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.Organizations.RequestDeletionAsync(
                "organization-a",
                new TerraScaleRequestOrganizationDeletionRequest(false, "Analytical Engines")));
        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.Organizations.UpdateMemberRoleAsync(
                "organization-a",
                "user-a",
                new TerraScaleUpdateOrganizationMemberRoleRequest("Administrator")));
        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.Organizations.RemoveMemberAsync(
                "organization-a",
                "user-a",
                new TerraScaleRemoveOrganizationMemberRequest(false)));

        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task OrganizationFailure_MapsPublishedErrorEnvelope()
    {
        const string responseBody = "{\"error\":{\"code\":\"forbidden\",\"message\":\"Organization access denied.\",\"category\":\"forbidden\"}}";
        using var handler = new ContractHandler(HttpStatusCode.Forbidden, responseBody);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Organizations.GetAsync("organization-a"));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(exception.ErrorCode).IsEqualTo("forbidden");
        await Assert.That(exception.Message).IsEqualTo("Organization access denied.");
    }

    [Test]
    public async Task NoContentOperations_RejectUnexpectedSuccessStatus()
    {
        using var handler = new ContractHandler(HttpStatusCode.OK, "{}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Organizations.LeaveAsync("organization-a"));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(
        httpClient,
        new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            BearerToken = "caller-token"
        });

    private sealed class ContractHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? fixedStatusCode;
        private readonly string? fixedResponseBody;

        public ContractHandler()
        {
        }

        public ContractHandler(HttpStatusCode fixedStatusCode, string fixedResponseBody)
        {
            this.fixedStatusCode = fixedStatusCode;
            this.fixedResponseBody = fixedResponseBody;
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri
                ?? throw new InvalidOperationException("The contract request URI is missing.");
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                requestUri.PathAndQuery,
                body,
                request.Headers.Authorization?.ToString()));

            if (fixedStatusCode is { } statusCode)
            {
                return Response(statusCode, fixedResponseBody ?? string.Empty);
            }

            return (request.Method.Method, requestUri.AbsolutePath) switch
            {
                ("POST", "/api/v1/organizations/organization-a:leave") =>
                    new HttpResponseMessage(HttpStatusCode.NoContent),
                ("POST", "/api/v1/organizations/organization-a/members/user-a:remove") =>
                    new HttpResponseMessage(HttpStatusCode.NoContent),
                ("GET", "/api/v1/organizations") => Response(HttpStatusCode.OK, $"[{OrganizationJson}]"),
                ("GET", "/api/v1/organizations/organization-a/members") =>
                    Response(HttpStatusCode.OK, $"[{MemberJson}]"),
                ("PATCH", "/api/v1/organizations/organization-a/members/user-a") =>
                    Response(HttpStatusCode.OK, MemberJson),
                ("GET", "/api/v1/organizations/organization-a/invitations") =>
                    Response(HttpStatusCode.OK, $"[{InvitationJson}]"),
                (_, "/api/v1/onboarding") or
                (_, "/api/v1/onboarding/organization") or
                (_, "/api/v1/onboarding/project") => Response(HttpStatusCode.OK, OnboardingJson),
                (_, "/api/v1/organizations/organization-a/invitations") or
                (_, "/api/v1/organizations/organization-a/invitations/invitation-a:accept") or
                (_, "/api/v1/organizations/organization-a/invitations/invitation-a:decline") or
                (_, "/api/v1/organizations/organization-a/invitations/invitation-a:resend") or
                (_, "/api/v1/organizations/organization-a/invitations/invitation-a:cancel") =>
                    Response(HttpStatusCode.OK, InvitationJson),
                _ => Response(HttpStatusCode.OK, OrganizationJson)
            };
        }

        private static HttpResponseMessage Response(HttpStatusCode statusCode, string responseBody) =>
            new(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
    }

    private const string OnboardingJson =
        "{\"userId\":\"user-a\",\"email\":\"ada@example.test\",\"displayName\":\"Ada\",\"phoneNumber\":\"+5511999999999\",\"accountInformationCompleted\":true,\"paymentMethodRegistered\":true,\"paymentMethodValidated\":true,\"paymentValidationStatus\":\"approved\",\"mfaDecision\":\"configured\",\"organizationId\":\"organization-a\",\"organizationOwnerAssigned\":true,\"paymentMethodLinkedToOrganization\":true,\"subscriptionOrTrialActive\":true,\"projectId\":\"project-a\",\"projectOwnerAssigned\":true,\"projectPrimaryRegions\":[\"GRU\",\"FRA\"],\"projectBackupRegion\":\"GRU\",\"completed\":true,\"status\":\"completed\",\"nextStep\":\"complete\",\"steps\":[{\"key\":\"organization\",\"state\":\"completed\"}],\"updatedAt\":\"2026-08-30T00:00:00Z\"}";

    private const string OrganizationJson =
        "{\"organizationId\":\"organization-a\",\"name\":\"Analytical Engines\",\"ownerUserIds\":[\"user-a\"],\"createdByUserId\":\"user-a\",\"status\":\"Active\",\"deletionRequestedAt\":null,\"deletionRequestedByUserId\":null,\"createdAt\":\"2026-08-30T00:00:00Z\"}";

    private const string MemberJson =
        "{\"userId\":\"user-a\",\"email\":\"ada@example.test\",\"displayName\":\"Ada\",\"role\":\"OrganizationOwner\",\"joinedAt\":\"2026-08-30T00:00:00Z\"}";

    private const string InvitationJson =
        "{\"invitationId\":\"invitation-a\",\"organizationId\":\"organization-a\",\"projectId\":\"project-a\",\"email\":\"grace@example.test\",\"role\":\"Member\",\"status\":\"Pending\",\"invitedByEmail\":\"ada@example.test\",\"expiresAt\":\"2026-09-06T00:00:00Z\",\"acceptedAt\":null,\"acceptedByUserId\":null,\"declinedAt\":null,\"canceledAt\":null,\"lastSentAt\":\"2026-08-30T00:00:00Z\",\"sentCount\":1,\"createdAt\":\"2026-08-30T00:00:00Z\"}";

    private sealed record RecordedRequest(
        string Method,
        string PathAndQuery,
        string? Body,
        string? Authorization);
}
