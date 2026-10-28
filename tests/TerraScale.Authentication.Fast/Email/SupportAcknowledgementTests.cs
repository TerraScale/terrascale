using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Email;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class SupportAcknowledgementTests(TerraBaseFixture fixture)
{
    private const string Collection = "email_support_deliveries";

    [Test]
    [Arguments("accepted")]
    [Arguments("retryable")]
    [Arguments("rejected")]
    [Arguments("unknown")]
    [Arguments("exception")]
    public async Task Support_acceptance_precedes_acknowledgement_and_repeats_preserve_both_outcomes(string acknowledgement)
    {
        var captured = new CapturingEmailSender();
        var sender = new ControlledSender(captured, EmailDeliveryOutcome.Accepted, acknowledgement);
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured,
            services: services => services.AddSingleton<IEmailSender>(sender));
        var (user, token) = await oidc.SignInUserAsync(new Faker().Internet.Email(provider: "example.test"));
        var organization = await oidc.CreateOrganizationAsync(token);
        var request = Request(user.Id, organization);
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var pending = SendAsync(oidc.Client, request);
        try
        {
            await sender.AcknowledgementEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var reserved = await store.GetAsync(Collection, OidcIssuer.InstanceId, request.RequestId);
            await Assert.That(reserved!.Document.GetProperty("status").GetString()).IsEqualTo("accepted");
            await Assert.That(reserved.Document.GetProperty("acknowledgementStatus").GetString()).IsEqualTo("sending");
            using var repeat = await SendAsync(oidc.Client, request);
            await AnswerAsync(repeat, "accepted", "unknown");
            await Assert.That(sender.Sent.Count).IsEqualTo(2);

            sender.ReleaseAcknowledgement.TrySetResult();
            var expected = acknowledgement == "exception" ? "unknown" : acknowledgement;
            var original = await pending;
            await AnswerAsync(original, "accepted", expected);
            using var recorded = await SendAsync(oidc.Client, request);
            await AnswerAsync(recorded, "accepted", expected);
            var document = await store.GetAsync(Collection, OidcIssuer.InstanceId, request.RequestId);
            await Assert.That(document!.Document.GetProperty("status").GetString()).IsEqualTo("accepted");
            await Assert.That(document.Document.GetProperty("acknowledgementStatus").GetString()).IsEqualTo(expected);
            await Assert.That(sender.Sent.Count(sent => sent == SupportEmailEndpoint.SupportAddress)).IsEqualTo(1);
            await Assert.That(sender.Sent.Count(sent => sent == user.Email)).IsEqualTo(1);
        }
        finally
        {
            sender.ReleaseAcknowledgement.TrySetResult();
            using var completed = await pending;
        }
    }

    [Test]
    [Arguments(EmailDeliveryOutcome.Unknown, "unknown", 1)]
    [Arguments(EmailDeliveryOutcome.Rejected, "rejected", 1)]
    [Arguments(EmailDeliveryOutcome.Retryable, "retryable", 2)]
    public async Task Only_known_unsent_support_attempts_can_repeat_and_none_send_an_acknowledgement(
        EmailDeliveryOutcome outcome, string expected, int sends)
    {
        var captured = new CapturingEmailSender();
        var sender = new ControlledSender(captured, outcome, "accepted");
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured,
            services: services => services.AddSingleton<IEmailSender>(sender));
        var (user, token) = await oidc.SignInUserAsync(new Faker().Internet.Email(provider: "example.test"));
        var request = Request(user.Id, await oidc.CreateOrganizationAsync(token));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await SendAsync(oidc.Client, request);
            await AnswerAsync(response, expected, null);
        }
        await Assert.That(sender.Sent.Count).IsEqualTo(sends);
        await Assert.That(sender.Sent.All(recipient => recipient == SupportEmailEndpoint.SupportAddress)).IsTrue();
        var document = await oidc.Server.App.Services.GetRequiredService<AuthStore>().GetAsync(Collection, OidcIssuer.InstanceId, request.RequestId);
        await Assert.That(document is null).IsEqualTo(outcome == EmailDeliveryOutcome.Retryable);
    }

    private static EmailSupportRequest Request(string user, string organization) =>
        new("01JA0000000000000000000A01", user, organization, null, null, "pro", "normal", "Write failure", "A synthetic support request.");

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, EmailSupportRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/terrascale/email/support") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OidcIssuer.DashboardClientId}:{OidcHarness.ClientSecret}")));
        return await client.SendAsync(request);
    }

    private static async Task AnswerAsync(HttpResponseMessage response, string status, string? acknowledgement)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo(status);
        await Assert.That(body.TryGetProperty("acknowledgementStatus", out var actual)).IsEqualTo(acknowledgement is not null);
        if (acknowledgement is not null)
        {
            await Assert.That(actual.GetString()).IsEqualTo(acknowledgement);
        }
    }

    private sealed class ControlledSender(CapturingEmailSender signIn, EmailDeliveryOutcome support, string acknowledgement) : IEmailSender
    {
        public ConcurrentQueue<string> Sent { get; } = [];
        public TaskCompletionSource AcknowledgementEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAcknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) => signIn.SendAsync(recipient, message, cancellationToken);

        public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        {
            if (message.Purpose is EmailPurpose.Authentication or EmailPurpose.Invitation)
            {
                // Authentication mail goes through the outbox's DeliverAsync too; it is captured, never a product outcome.
                await signIn.SendAsync(recipient, message, cancellationToken);
                return EmailDeliveryOutcome.Accepted;
            }

            this.Sent.Enqueue(recipient);
            if (recipient == SupportEmailEndpoint.SupportAddress)
            {
                return support;
            }
            this.AcknowledgementEntered.TrySetResult();
            await this.ReleaseAcknowledgement.Task.WaitAsync(cancellationToken);
            return acknowledgement switch
            {
                "accepted" => EmailDeliveryOutcome.Accepted,
                "retryable" => EmailDeliveryOutcome.Retryable,
                "rejected" => EmailDeliveryOutcome.Rejected,
                "unknown" => EmailDeliveryOutcome.Unknown,
                _ => throw new HttpRequestException("Controlled ambiguous acknowledgement failure."),
            };
        }
    }
}
