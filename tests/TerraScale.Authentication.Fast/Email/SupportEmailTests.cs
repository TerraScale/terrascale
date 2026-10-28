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
public sealed class SupportEmailTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_request_reaches_support_with_context_and_is_acknowledged_once()
    {
        var captured = new CapturingEmailSender();
        var sender = new Sender(captured);
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured, services: services => services.AddSingleton<IEmailSender>(sender));
        var (user, _, tokens) = await oidc.SignInAndRedeemAsync("support-requester@example.test");
        var organization = await oidc.OkAsync(HttpMethod.Post, Organizations, tokens.GetProperty("access_token").GetString()!, new { name = "Support fixture" }, HttpStatusCode.Created);
        var request = Request("01JA0000000000000000000S01", user.Id, organization.GetProperty("id").GetString()!);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var response = await SendAsync(oidc.Client, request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()).IsEqualTo("accepted");
        }
        var support = sender.Sent.Single(sent => sent.Recipient == SupportEmailEndpoint.SupportAddress);
        await Assert.That(support.Message.ReplyTo).IsEqualTo("support-requester@example.test");
        await Assert.That(support.Message.Subject).Contains("[urgent] Writes fail");
        await Assert.That(support.Message.Text).Contains(request.OrganizationId).And.Contains("Support fixture").And.Contains("Plan: pro");
        var acknowledgement = sender.Sent.Single(sent => sent.Recipient == "support-requester@example.test");
        await Assert.That(acknowledgement.Message.ReplyTo).IsEqualTo(SupportEmailEndpoint.SupportAddress);
        await Assert.That(acknowledgement.Message.Text).Contains(request.RequestId).And.Contains("1 business day");
        await Assert.That(sender.Sent.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Strangers_invalid_requests_and_floods_send_nothing_more()
    {
        var captured = new CapturingEmailSender();
        var sender = new Sender(captured);
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured, services: services => services.AddSingleton<IEmailSender>(sender));
        var (owner, _, tokens) = await oidc.SignInAndRedeemAsync("support-owner@example.test");
        var (stranger, _, _) = await oidc.SignInAndRedeemAsync("support-stranger@example.test");
        var organization = (await oidc.OkAsync(HttpMethod.Post, Organizations, tokens.GetProperty("access_token").GetString()!, new { name = "Flood fixture" }, HttpStatusCode.Created))
            .GetProperty("id").GetString()!;

        using var anonymous = new HttpRequestMessage(HttpMethod.Post, "/v1/terrascale/email/support") { Content = JsonContent.Create(Request("01JA0000000000000000000S02", owner.Id, organization)) };
        using var refused = await oidc.Client.SendAsync(anonymous);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var outsider = await SendAsync(oidc.Client, Request("01JA0000000000000000000S03", stranger.Id, organization));
        await Assert.That(outsider.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var invalid = await SendAsync(oidc.Client, Request("01JA0000000000000000000S04", owner.Id, organization) with { Subject = "two\nlines" });
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(sender.Sent.Count).IsEqualTo(0);

        for (var index = 0; index < SupportEmailEndpoint.PerUserPerHour; index++)
        {
            using var accepted = await SendAsync(oidc.Client, Request($"01JA0000000000000000000T{index:D2}", owner.Id, organization));
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        using var limited = await SendAsync(oidc.Client, Request("01JA0000000000000000000T99", owner.Id, organization));
        await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(sender.Sent.Count(sent => sent.Recipient == SupportEmailEndpoint.SupportAddress)).IsEqualTo(SupportEmailEndpoint.PerUserPerHour);
    }

    private static EmailSupportRequest Request(string requestId, string userId, string organizationId) =>
        new(requestId, userId, organizationId, null, null, "pro", "urgent", "Writes fail", "Every write answers 503 since 09:00 UTC.");

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, EmailSupportRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/terrascale/email/support") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OidcIssuer.DashboardClientId}:{OidcHarness.ClientSecret}")));
        return await client.SendAsync(request);
    }

    private sealed class Sender(CapturingEmailSender signIn) : IEmailSender
    {
        public ConcurrentQueue<(string Recipient, EmailMessage Message)> Sent { get; } = [];
        public Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) => signIn.SendAsync(recipient, message, cancellationToken);
        public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        {
            if (message.Purpose is EmailPurpose.Authentication or EmailPurpose.Invitation)
            {
                // Authentication mail goes through the outbox's DeliverAsync too; it is captured, never a product outcome.
                await signIn.SendAsync(recipient, message, cancellationToken);
                return EmailDeliveryOutcome.Accepted;
            }

            Sent.Enqueue((recipient, message));
            return EmailDeliveryOutcome.Accepted;
        }
    }
}
