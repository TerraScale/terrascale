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

namespace TerraScale.Authentication.Fast.Email;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AllowanceEmailTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task The_dashboard_sends_an_allowance_warning_to_the_organization_owner()
    {
        var captured = new CapturingEmailSender();
        var sender = new Sender(captured);
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured, services: services => services.AddSingleton<IEmailSender>(sender));
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync("allowance-owner@example.test");
        var organization = await oidc.OkAsync(HttpMethod.Post, Organizations, tokens.GetProperty("access_token").GetString()!,
            new { name = "Allowance fixture" }, HttpStatusCode.Created);
        var id = organization.GetProperty("id").GetString()!;
        using var response = await SendAsync(oidc.Client, Request(id));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.EnumerateObject().Count()).IsEqualTo(1);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("accepted");
        await Assert.That(sender.ProductMessages.Count).IsEqualTo(1);
        await Assert.That(sender.ProductMessages.Single().Subject).Contains("80%");
        await Assert.That(sender.ProductMessages.Single().Text).Contains($"/usage?organizationId={id}");
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
        public Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) => signIn.SendAsync(recipient, message, cancellationToken);
        public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        {
            if (message.Purpose is EmailPurpose.Authentication or EmailPurpose.Invitation)
            {
                // Authentication mail goes through the outbox's DeliverAsync too; it is captured, never a product outcome.
                await signIn.SendAsync(recipient, message, cancellationToken);
                return EmailDeliveryOutcome.Accepted;
            }

            ProductMessages.Add(message);
            return EmailDeliveryOutcome.Accepted;
        }
    }
}
