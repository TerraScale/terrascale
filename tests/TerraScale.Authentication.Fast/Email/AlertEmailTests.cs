using System.Collections.Concurrent;
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

namespace TerraScale.Authentication.Fast.Email;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AlertEmailTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Each_alert_transition_emails_the_owners_once_and_invalid_bodies_send_nothing()
    {
        var captured = new CapturingEmailSender();
        var sender = new Sender(captured);
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured, services: services => services.AddSingleton<IEmailSender>(sender));
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync("alert-owner@example.test");
        var access = tokens.GetProperty("access_token").GetString()!;
        var organization = (await oidc.OkAsync(HttpMethod.Post, Organizations, access, new { name = "Alert fixture" }, HttpStatusCode.Created)).GetProperty("id").GetString()!;
        var project = (await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organization}/projects", access, new { name = "Alerted" }, HttpStatusCode.Created))
            .GetProperty("id").GetString()!;
        var firing = new EmailAlertRequest(organization, project, "01JA00000000000000000000A7", "firing", "Slow reads", "p95_latency_ms", "main", 250, 412.5);

        foreach (var invalid in new[] { firing with { State = "pending" }, firing with { RuleName = new string('x', 101) }, firing with { Metric = "cpu" },
            firing with { DatabaseName = "Main" }, firing with { Value = null }, firing with { AlertId = "alert" } })
        {
            using var refused = await SendAsync(oidc.Client, invalid);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
        await Assert.That(sender.Attempts).IsEqualTo(0);

        await Assert.That(await StatusAsync(oidc.Client, firing)).IsEqualTo("accepted");
        await Assert.That(await StatusAsync(oidc.Client, firing with { Value = 500 })).IsEqualTo("accepted");
        var message = sender.ProductMessages.Single();
        await Assert.That(message.Subject).IsEqualTo("[TerraScale] Alert firing: Slow reads");
        await Assert.That(message.Text).Contains("database main");
        await Assert.That(message.Text).Contains("412.5 ms (threshold 250 ms)");
        await Assert.That(message.Text).Contains("https://dash.terrascale.tech/notifications/alerts");

        await Assert.That(await StatusAsync(oidc.Client, firing with { State = "resolved", Value = 120 })).IsEqualTo("accepted");
        await Assert.That(sender.ProductMessages.Count).IsEqualTo(2);
        await Assert.That(sender.ProductMessages.Count(sent => sent.Subject == "[TerraScale] Alert resolved: Slow reads")).IsEqualTo(1);
    }

    private static async Task<string?> StatusAsync(HttpClient client, EmailAlertRequest body)
    {
        using var response = await SendAsync(client, body);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, EmailAlertRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/terrascale/email/alert") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OidcIssuer.DashboardClientId}:{OidcHarness.ClientSecret}")));
        return await client.SendAsync(request);
    }

    private sealed class Sender(CapturingEmailSender signIn) : IEmailSender
    {
        public ConcurrentBag<EmailMessage> ProductMessages { get; } = [];
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
            ProductMessages.Add(message);
            return EmailDeliveryOutcome.Accepted;
        }
    }
}
