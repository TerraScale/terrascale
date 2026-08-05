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
        await Assert.That(exception.ErrorCode).IsEqualTo("TERRASCALE-ADMIN-OWNER-REQUIRED");
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
                : "{\"code\":\"TERRASCALE-ADMIN-OWNER-REQUIRED\",\"message\":\"Platform owner role required.\"}";
            return new HttpResponseMessage(statusCode) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private sealed record RecordedRequest(string Uri, string? Body, string? IdempotencyKey);
}
