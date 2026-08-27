using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAdministrationClientTests
{
    [Test]
    public async Task Administration_UsesEveryPublishedAdminRouteAndTypedModels()
    {
        using var handler = new AdministrationHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var admin = CreateClient(httpClient).Administration;

        await admin.GetOverviewAsync();
        await admin.ListUsersAsync(skip: 2, take: 3, search: "Ada Lovelace");
        await admin.GetUserAsync("user/a");
        await admin.EnableUserAsync("user/a");
        await admin.DisableUserAsync("user/a", "support request");
        await admin.DeleteUserAsync("user/a");
        await admin.CreateSupportNoteAsync(new TerraScaleAdminCreateSupportNoteRequest(null, null, "project/a", "follow up"));
        await admin.ListOrganizationsAsync(skip: 2, take: 3);
        await admin.ListProjectsAsync(skip: 2, take: 3);
        await admin.ListDatabasesAsync(skip: 2, take: 3);
        await admin.GetUsageAsync();
        await admin.ListPricingPlansAsync();
        await admin.CreatePricingPlanAsync(new TerraScaleAdminPricingPlanRequest(
            Name: "Developer",
            MonthlyPolarProductPriceId: "polar-monthly-price",
            AnnualPolarProductPriceId: "polar-annual-price"));
        await admin.UpdatePricingPlanAsync("plan/a", new TerraScaleAdminPricingPlanRequest(Visible: true));
        await admin.DeletePricingPlanAsync("plan/a");
        await admin.ListAuditAsync(skip: 2, take: 3);
        await admin.ListNotificationsAsync(skip: 2, take: 3);
        await admin.GetPaymentWebhookAsync("polar", "event/a");
        await admin.ListDeadLetteredPaymentWebhooksAsync(skip: 2, take: 3);
        await admin.ReplayPaymentWebhookAsync("polar", "event/a", "webhook-replay-key");

        await Assert.That(handler.Requests.Select(request => request.Uri)).IsEquivalentTo([
            "https://api.example.invalid/api/v1/admin",
            "https://api.example.invalid/api/v1/admin/users?skip=2&take=3&search=Ada Lovelace",
            "https://api.example.invalid/api/v1/admin/users/user%2Fa",
            "https://api.example.invalid/api/v1/admin/users/user%2Fa:enable",
            "https://api.example.invalid/api/v1/admin/users/user%2Fa:disable",
            "https://api.example.invalid/api/v1/admin/users/user%2Fa",
            "https://api.example.invalid/api/v1/admin/support-notes",
            "https://api.example.invalid/api/v1/admin/organizations?skip=2&take=3",
            "https://api.example.invalid/api/v1/admin/projects?skip=2&take=3",
            "https://api.example.invalid/api/v1/admin/databases?skip=2&take=3",
            "https://api.example.invalid/api/v1/admin/usage",
            "https://api.example.invalid/api/v1/admin/plans",
            "https://api.example.invalid/api/v1/admin/plans",
            "https://api.example.invalid/api/v1/admin/plans/plan%2Fa",
            "https://api.example.invalid/api/v1/admin/plans/plan%2Fa",
            "https://api.example.invalid/api/v1/admin/audit?skip=2&take=3",
            "https://api.example.invalid/api/v1/admin/notifications?skip=2&take=3",
            "https://api.example.invalid/api/v1/admin/billing/webhooks/polar/event%2Fa",
            "https://api.example.invalid/api/v1/admin/billing/webhooks/dead-lettered?skip=2&take=3",
            "https://api.example.invalid/api/v1/admin/billing/webhooks/polar/event%2Fa:replay"
        ]);
        await Assert.That(handler.Requests.Single(request => request.Uri.EndsWith(":disable", StringComparison.Ordinal)).Body).Contains("support request");
        var planCreate = handler.Requests.Single(request => request.Uri.EndsWith("/admin/plans", StringComparison.Ordinal) && request.Body?.Contains("polar-monthly-price", StringComparison.Ordinal) is true);
        await Assert.That(planCreate.Body).Contains("monthlyPolarProductPriceId");
        await Assert.That(planCreate.Body).Contains("annualPolarProductPriceId");
        await Assert.That(handler.Requests.Single(request => request.Uri.EndsWith(":replay", StringComparison.Ordinal)).IdempotencyKey).IsEqualTo("webhook-replay-key");
    }

    [Test]
    public async Task Administration_MapsOwnerAuthorizationFailureToTypedApiException()
    {
        using var handler = new AdministrationHandler(HttpStatusCode.Forbidden);
        using var httpClient = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() => CreateClient(httpClient).Administration.GetOverviewAsync());

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(exception.ErrorCode).IsEqualTo("admin_owner_required");
    }

    [Test]
    public async Task BillingReconciliation_UsesTypedRoutesReplayHeaderAndLifecycleResponse()
    {
        using var handler = new ReconciliationHandler();
        using var httpClient = new HttpClient(handler);
        var administration = CreateClient(httpClient).Administration;

        var triggered = await administration.TriggerBillingReconciliationAsync("organization / a", "reconciliation-key");
        var status = await administration.GetBillingReconciliationAsync("organization / a", "attempt / a");

        await Assert.That(handler.Requests).IsEquivalentTo([
            new ReconciliationRecordedRequest(
                HttpMethod.Post,
                "https://api.example.invalid/api/v1/admin/billing/reconciliation/organization %2F a:trigger",
                null,
                "reconciliation-key"),
            new ReconciliationRecordedRequest(
                HttpMethod.Get,
                "https://api.example.invalid/api/v1/admin/billing/reconciliation/organization %2F a/attempt %2F a",
                null,
                null)
        ]);
        await Assert.That(triggered.AttemptId).IsEqualTo("attempt-a");
        await Assert.That(triggered.Provider).IsEqualTo("polar");
        await Assert.That(triggered.EnqueuedAt).IsNotNull();
        await Assert.That(triggered.StartedAt).IsNull();
        await Assert.That(status.State).IsEqualTo("completed");
        await Assert.That(status.ReconciledAt).IsNotNull();
        await Assert.That(status.ErrorCode).IsNull();
    }

    [Test]
    public async Task BillingReconciliation_RejectsInvalidArgumentsAndMapsOwnerAuthorizationFailure()
    {
        using var validationClient = new HttpClient(new AdministrationHandler(HttpStatusCode.OK));
        var validation = CreateClient(validationClient).Administration;
        var blankOrganization = await Assert.ThrowsAsync<ArgumentException>(() =>
            validation.TriggerBillingReconciliationAsync(" ", "reconciliation-key"));
        var blankKey = await Assert.ThrowsAsync<ArgumentException>(() =>
            validation.TriggerBillingReconciliationAsync("organization-a", " "));
        var oversizedKey = await Assert.ThrowsAsync<ArgumentException>(() =>
            validation.TriggerBillingReconciliationAsync("organization-a", new string('x', 129)));
        var blankAttempt = await Assert.ThrowsAsync<ArgumentException>(() =>
            validation.GetBillingReconciliationAsync("organization-a", " "));

        using var forbiddenHandler = new AdministrationHandler(HttpStatusCode.Forbidden);
        using var forbiddenClient = new HttpClient(forbiddenHandler);
        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            CreateClient(forbiddenClient).Administration.TriggerBillingReconciliationAsync("organization-a", "reconciliation-key"));

        await Assert.That(blankOrganization).IsNotNull();
        await Assert.That(blankKey).IsNotNull();
        await Assert.That(oversizedKey).IsNotNull();
        await Assert.That(blankAttempt).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(exception.ErrorCode).IsEqualTo("admin_owner_required");
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        BearerToken = "test-token"
    });

    private sealed class AdministrationHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues("Idempotency-Key", out var idempotencyKeys);
            Requests.Add(new RecordedRequest(request.RequestUri!.ToString(), body, idempotencyKeys?.SingleOrDefault()));
            var response = statusCode == HttpStatusCode.OK
                ? request.Method == HttpMethod.Delete ? "true" : request.RequestUri!.AbsolutePath.EndsWith("/plans", StringComparison.Ordinal) && request.Method == HttpMethod.Get ? "[]" : "{}"
                : "{\"code\":\"admin_owner_required\",\"message\":\"Platform owner role required.\"}";
            return new HttpResponseMessage(statusCode) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ReconciliationHandler : HttpMessageHandler
    {
        public List<ReconciliationRecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new ReconciliationRecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("Idempotency-Key", out var idempotencyKeys) ? idempotencyKeys.SingleOrDefault() : null));

            var body = request.Method == HttpMethod.Post
                ? "{\"attemptId\":\"attempt-a\",\"organizationId\":\"organization-a\",\"provider\":\"polar\",\"state\":\"enqueued\",\"requestedAt\":\"2026-08-16T06:00:00+00:00\",\"enqueuedAt\":\"2026-08-16T06:00:01+00:00\",\"startedAt\":null,\"finishedAt\":null,\"reconciledAt\":null,\"errorCode\":null}"
                : "{\"attemptId\":\"attempt-a\",\"organizationId\":\"organization-a\",\"provider\":\"polar\",\"state\":\"completed\",\"requestedAt\":\"2026-08-16T06:00:00+00:00\",\"enqueuedAt\":\"2026-08-16T06:00:01+00:00\",\"startedAt\":\"2026-08-16T06:00:02+00:00\",\"finishedAt\":\"2026-08-16T06:00:03+00:00\",\"reconciledAt\":\"2026-08-16T06:00:03+00:00\",\"errorCode\":null}";

            return new HttpResponseMessage(request.Method == HttpMethod.Post ? HttpStatusCode.Accepted : HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(string Uri, string? Body, string? IdempotencyKey);
    private sealed record ReconciliationRecordedRequest(HttpMethod Method, string Uri, string? Body, string? IdempotencyKey);
}
