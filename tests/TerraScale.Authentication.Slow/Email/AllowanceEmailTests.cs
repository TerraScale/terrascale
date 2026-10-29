using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Slow.Email;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AllowanceEmailTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Repeated_and_concurrent_delivery_sends_once_without_disclosing_owners()
    {
        var captured = new CapturingEmailSender();
        var sender = new Sender(captured);
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured, services: services => services.AddSingleton<IEmailSender>(sender));
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync("allowance-owner@example.test");
        var access = tokens.GetProperty("access_token").GetString()!;
        var organization = await oidc.OkAsync(HttpMethod.Post, Organizations, access, new { name = "Allowance fixture" }, HttpStatusCode.Created);
        var id = organization.GetProperty("id").GetString()!;
        var request = Request(id);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SendAsync(oidc.Client, request)));
        foreach (var response in responses)
        {
            using (response)
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                await Assert.That(body.EnumerateObject().Count()).IsEqualTo(1);
                await Assert.That(body.GetProperty("status").GetString() is "accepted" or "unknown").IsTrue();
            }
        }
        await Assert.That(sender.ProductMessages.Count).IsEqualTo(1);
        await Assert.That(sender.ProductMessages.Single().Subject).Contains("80%");
        await Assert.That(sender.ProductMessages.Single().Text).Contains($"/usage?organizationId={id}");
        using var repeated = await SendAsync(oidc.Client, request with { Used = 99 });
        await Assert.That(sender.ProductMessages.Count).IsEqualTo(1);
        using var reached = await SendAsync(oidc.Client, request with { Threshold = 100, Used = 100 });
        await Assert.That(reached.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(sender.ProductMessages.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Only_dashboard_client_can_send_and_invalid_thresholds_send_nothing()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        using var anonymous = new HttpRequestMessage(HttpMethod.Post, "/v1/terrascale/email/allowance") { Content = JsonContent.Create(Request("01JA0000000000000000000001")) };
        using var refused = await oidc.Client.SendAsync(anonymous);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var invalid = await SendAsync(oidc.Client, Request("01JA0000000000000000000001") with { Used = 79 });
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task Known_refusal_retries_but_ambiguous_acceptance_does_not()
    {
        var captured = new CapturingEmailSender();
        var sender = new Sender(captured) { Outcome = EmailDeliveryOutcome.Retryable };
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured, services: services => services.AddSingleton<IEmailSender>(sender));
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync("allowance-retry@example.test");
        var organization = await oidc.OkAsync(HttpMethod.Post, Organizations, tokens.GetProperty("access_token").GetString()!, new { name = "Retry fixture" }, HttpStatusCode.Created);
        var request = Request(organization.GetProperty("id").GetString()!);
        using var retry = await SendAsync(oidc.Client, request);
        await Assert.That((await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()).IsEqualTo("retryable");
        sender.Outcome = EmailDeliveryOutcome.Unknown;
        using var unknown = await SendAsync(oidc.Client, request);
        using var repeated = await SendAsync(oidc.Client, request);
        await Assert.That(sender.Attempts).IsEqualTo(2);
        await Assert.That((await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()).IsEqualTo("unknown");
    }

    private static AllowanceEmailRequest Request(string organizationId) => new(organizationId,
        DateTimeOffset.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture), "operations", 80, 81, 100);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, AllowanceEmailRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/terrascale/email/allowance") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OidcIssuer.DashboardClientId}:{OidcHarness.ClientSecret}")));
        return await client.SendAsync(request);
    }

    private sealed class Sender(CapturingEmailSender signIn) : IEmailSender
    {
        public ConcurrentBag<EmailMessage> ProductMessages { get; } = [];
        public EmailDeliveryOutcome Outcome { get; set; } = EmailDeliveryOutcome.Accepted;
        public int Attempts;
        public Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) => signIn.SendAsync(recipient, message, cancellationToken);
        public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        {
            if (message.Purpose is EmailPurpose.Authentication or EmailPurpose.Invitation)
            {
                // Authentication mail goes through the outbox's DeliverAsync too; it is captured, never a product outcome.
                await signIn.SendAsync(recipient, message, cancellationToken);
                return EmailDeliveryOutcome.Accepted;
            }

            _ = Interlocked.Increment(ref Attempts);
            if (Outcome == EmailDeliveryOutcome.Accepted)
            {
                ProductMessages.Add(message);
            }
            return Outcome;
        }
    }
}
