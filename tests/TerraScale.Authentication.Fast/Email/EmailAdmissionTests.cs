using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Email;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class EmailAdmissionTests(TerraBaseFixture fixture)
{
    [Test]
    [Arguments(EmailPurpose.Test, 20)]
    [Arguments(EmailPurpose.Alert, 100)]
    [Arguments(EmailPurpose.Notification, 50)]
    public async Task Noncritical_class_and_tenant_limits_preserve_authentication_capacity(EmailPurpose purpose, int limit)
    {
        var clock = new StoreTestClock();
        using var provider = new Provider();
        using var http = new HttpClient(provider);
        var reservations = Enumerable.Range(0, limit).Select(index => new EmailReservation(clock.UtcNow, purpose,
            purpose == EmailPurpose.Notification ? "limited" : index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        await using var server = await StartAsync(http, clock, reservations);
        await Assert.That(await SendAsync(server, purpose, "limited")).IsEqualTo("Retryable");
        await Assert.That(await SendAsync(server, EmailPurpose.Authentication, "limited", 1)).IsEqualTo("Accepted");
        await Assert.That(provider.Calls).IsEqualTo(1);
        await Assert.That((await BudgetAsync(server)).Reservations.Count).IsEqualTo(limit + 1);
    }

    [Test]
    public async Task Two_instances_share_the_last_noncritical_slot_through_database_CAS()
    {
        var clock = new StoreTestClock();
        using var provider = new Provider();
        using var http = new HttpClient(provider);
        await using var server = await StartAsync(http, clock, FullNoncriticalBudget(clock));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => SendAsync(server, EmailPurpose.Notification, AuthStore.NewId(), index % 2)));
        await Assert.That(outcomes.Count(outcome => outcome == "Accepted")).IsEqualTo(1);
        await Assert.That(outcomes.Count(outcome => outcome == "Retryable")).IsEqualTo(7);
        await Assert.That(provider.Calls).IsEqualTo(1);
        await Assert.That((await BudgetAsync(server)).Reservations.Count).IsEqualTo(500);
        await Assert.That(await SendAsync(server, EmailPurpose.Authentication)).IsEqualTo("Accepted");
        await Assert.That((await BudgetAsync(server)).Reservations.Count).IsEqualTo(501);
    }

    [Test]
    public async Task Provider_429_pauses_both_instances_until_retry_after_without_repeating_the_send()
    {
        var clock = new StoreTestClock();
        using var provider = new Provider { Status = HttpStatusCode.TooManyRequests, Reset = clock.UtcNow.AddMinutes(5) };
        using var http = new HttpClient(provider);
        await using var server = await StartAsync(http, clock, []);
        await Assert.That(await SendAsync(server, EmailPurpose.Authentication)).IsEqualTo("Retryable");
        await Assert.That((await BudgetAsync(server)).PausedUntil).IsEqualTo(provider.Reset.Value);
        clock.UtcNow = provider.Reset.Value.AddTicks(-1);
        await Assert.That(await SendAsync(server, EmailPurpose.Authentication, node: 1)).IsEqualTo("Retryable");
        await Assert.That(provider.Calls).IsEqualTo(1);
        clock.UtcNow = provider.Reset.Value;
        provider.Status = HttpStatusCode.OK;
        await Assert.That(await SendAsync(server, EmailPurpose.Authentication, node: 1)).IsEqualTo("Accepted");
        await Assert.That(provider.Calls).IsEqualTo(2);
        await Assert.That((await BudgetAsync(server)).Reservations.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(HttpStatusCode.OK, "Accepted")]
    [Arguments(HttpStatusCode.ServiceUnavailable, "Unknown")]
    public async Task Accepted_and_unknown_sends_keep_their_budget_and_are_not_automatically_repeated(HttpStatusCode status, string expected)
    {
        var clock = new StoreTestClock();
        using var provider = new Provider { Status = status };
        using var http = new HttpClient(provider);
        await using var server = await StartAsync(http, clock, FullNoncriticalBudget(clock));
        await Assert.That(await SendAsync(server, EmailPurpose.Notification)).IsEqualTo(expected);
        await Assert.That(await SendAsync(server, EmailPurpose.Notification, node: 1)).IsEqualTo("Retryable");
        await Assert.That(provider.Calls).IsEqualTo(1);
        await Assert.That((await BudgetAsync(server)).Reservations.Count).IsEqualTo(500);
    }

    [Test]
    public async Task Admission_endpoint_authenticates_validates_and_reserves_once_without_sending_mail()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        await using var server = await AuthTestServer.StartAsync(settings: settings =>
        {
            settings["cloudflare-account-id"] = "synthetic-account";
            settings["cloudflare-email-token"] = "synthetic-token";
        }, services: services => fixture.UseStorage(services, tenant));
        await Assert.That(server.App.Services.GetRequiredService<IEmailSender>() is AdmittedEmailSender).IsTrue();
        var credential = EmailAdmissionEndpoint.Credential("synthetic-token");
        var attemptId = AuthStore.NewId();
        var body = Body(attemptId);
        await CheckAsync(body, HttpStatusCode.Unauthorized, "");
        await CheckAsync(body, HttpStatusCode.Unauthorized, "synthetic-token");
        await CheckAsync(body, HttpStatusCode.OK);
        await CheckAsync(body, HttpStatusCode.TooManyRequests);
        await CheckAsync("{", HttpStatusCode.BadRequest);
        await CheckAsync(body, HttpStatusCode.UnsupportedMediaType, mediaType: "text/plain");
        await CheckAsync(Body(NUlid.Ulid.NewUlid(DateTimeOffset.UtcNow.AddHours(-1)).ToString()), HttpStatusCode.BadRequest);
        await CheckAsync(Body(NUlid.Ulid.NewUlid(DateTimeOffset.UtcNow.AddHours(1)).ToString()), HttpStatusCode.BadRequest);
        var budget = await BudgetAsync(server);
        await Assert.That(budget.Reservations.Count).IsEqualTo(1);
        await Assert.That(budget.Reservations.Single().AttemptId).IsEqualTo(attemptId);
        await Assert.That(budget.Reservations.Single().Purpose).IsEqualTo(EmailPurpose.Alert);
        await Assert.That(budget.Reservations.Single().Tenant).IsEqualTo("ts-alerts");
        var store = server.App.Services.GetRequiredService<AuthStore>();
        var document = (await store.GetAsync(EmailAdmission.Collection, OidcIssuer.InstanceId, "cloudflare"))!;
        budget.Reservations.AddRange(Enumerable.Range(0, 49).Select(_ => new EmailReservation(DateTimeOffset.UtcNow, EmailPurpose.Alert, "ts-alerts")));
        await Assert.That(await store.CompareAndPutAsync(EmailAdmission.Collection, OidcIssuer.InstanceId, "cloudflare",
            JsonSerializer.SerializeToElement(budget, EmailAdmissionJson.Default.EmailBudget), document.Version)).IsTrue();
        await CheckAsync(Body(AuthStore.NewId()), HttpStatusCode.TooManyRequests);
        await Assert.That((await BudgetAsync(server)).Reservations.Count).IsEqualTo(50);

        static string Body(string id) => JsonSerializer.Serialize(new EmailAdmissionRequest(id), EmailAdmissionApiJson.Default.EmailAdmissionRequest);
        async Task CheckAsync(string json, HttpStatusCode expected, string? authorization = null, string mediaType = "application/json")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/terrascale/email/admission")
            { Content = new StringContent(json, System.Text.Encoding.UTF8, mediaType) };
            if (authorization != "")
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization ?? credential);
            }
            var requestedAt = DateTimeOffset.UtcNow;
            using var response = await server.Client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(expected);
            if (expected == HttpStatusCode.TooManyRequests)
            {
                await Assert.That(response.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(60));
            }
            if (expected == HttpStatusCode.OK)
            {
                var admitted = (await response.Content.ReadFromJsonAsync(EmailAdmissionApiJson.Default.EmailAdmissionResponse))!;
                await Assert.That(admitted.Status).IsEqualTo("admitted");
                await Assert.That(admitted.SendBy >= requestedAt.AddSeconds(10) && admitted.SendBy <= DateTimeOffset.UtcNow.AddSeconds(10)).IsTrue();
            }
        }
    }

    private async Task<AuthTestServer> StartAsync(HttpClient http, StoreTestClock clock, List<EmailReservation> reservations)
    {
        var (storageTenant, database) = await fixture.CreateDatabaseAsync();
        var store = new AuthStore(database, clock);
        _ = await store.CreateAsync(EmailAdmission.Collection, OidcIssuer.InstanceId, "cloudflare",
            JsonSerializer.SerializeToElement(new EmailBudget(OidcIssuer.InstanceId, reservations, default), EmailAdmissionJson.Default.EmailBudget));
        var provider = new CloudflareEmailSender(http, new CloudflareEmailOptions("synthetic-account", "synthetic-token"), NullLogger<CloudflareEmailSender>.Instance);
        var senders = new[] { store, new AuthStore(database, clock) }.Select(shared =>
            new AdmittedEmailSender(new EmailAdmission(shared, clock), provider, NullLogger<AdmittedEmailSender>.Instance)).ToArray();
        var recipient = new Faker().Internet.Email(provider: "example.invalid");
        return await AuthTestServer.StartAsync(services: services =>
        {
            fixture.UseStorage(services, storageTenant);
            services.AddSingleton<TimeProvider>(clock);
        }, routes: app => app.MapPost("/__test/email/{node:int}/{purpose}/{tenant}",
            async (int node, EmailPurpose purpose, string tenant, CancellationToken cancellationToken) => Results.Text(
                (await senders[node].DeliverAsync(recipient, new EmailMessage("Synthetic mail", "Integration fixture")
                { Purpose = purpose, Tenant = tenant }, cancellationToken)).ToString())));
    }

    private static List<EmailReservation> FullNoncriticalBudget(StoreTestClock clock)
    {
        EmailPurpose[] purposes = [EmailPurpose.Invitation, EmailPurpose.Alert, EmailPurpose.Allowance, EmailPurpose.Support, EmailPurpose.Notification];
        // Keep each class below its own ceiling so only the shared 500-message budget decides the race.
        return Enumerable.Range(0, 499).Select(index => new EmailReservation(clock.UtcNow,
            index < 20 ? EmailPurpose.Test : purposes[(index - 20) % purposes.Length], AuthStore.NewId())).ToList();
    }

    private static async Task<string> SendAsync(AuthTestServer server, EmailPurpose purpose, string tenant = "platform", int node = 0)
    {
        using var response = await server.Client.PostAsync($"/__test/email/{node}/{purpose}/{tenant}", null);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<EmailBudget> BudgetAsync(AuthTestServer server) =>
        (await server.App.Services.GetRequiredService<AuthStore>().GetAsync(EmailAdmission.Collection, OidcIssuer.InstanceId, "cloudflare"))!
        .Document.Deserialize(EmailAdmissionJson.Default.EmailBudget)!;

    // This terminal handler cannot reach Cloudflare or follow a redirect; every attempt completes immediately.
    private sealed class Provider : HttpMessageHandler
    {
        public int Calls;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public DateTimeOffset? Reset { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref this.Calls);
            using var sent = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var recipient = sent.RootElement.GetProperty("to").GetRawText();
            var response = new HttpResponseMessage(this.Status)
            {
                Content = new StringContent($$"""{"success":true,"result":{"queued":[{{recipient}}],"delivered":[],"permanent_bounces":[],"suppressed_recipients":[]}}"""),
            };
            if (this.Reset is { } reset)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(reset);
            }
            return response;
        }
    }
}
