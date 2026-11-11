using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Email;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class SignUpEmailTests(TerraBaseFixture fixture)
{
    private const string SignUp = $"/ui/{OidcIssuer.Slug}/sign-up";
    private const string Password = "A sound body keeps a sound mind!";

    [Test]
    public async Task A_sign_up_hands_one_code_email_to_smtp_without_any_timer_and_the_code_completes_the_sign_up()
    {
        await using var mail = await SmtpMailFixture.StartAsync();
        await using var cloudflare = await EmailProviderFixture.FakeCloudflare.StartAsync();
        var frozen = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var start = frozen.GetUtcNow();
        await using var oidc = await StartAsync(fixture, mail, cloudflare, frozen);

        var email = oidc.UniqueEmail("smtp-signup");
        using var started = await oidc.PostFormAsync(SignUp, new() { ["email"] = email, ["password"] = Password });
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var pending = PendingToken().Match(await started.Content.ReadAsStringAsync()).Groups[1].Value;
        await Assert.That(pending).IsNotEmpty();

        var accepted = await mail.WaitForAsync(email);
        await Assert.That(frozen.GetUtcNow()).IsEqualTo(start);
        await Assert.That(mail.Accepted.Count(message => message.To.Contains(email, StringComparer.Ordinal))).IsEqualTo(1);
        await Assert.That(accepted.From).IsEqualTo("no-reply@terrascale.tech");
        await Assert.That(accepted.Message.From.Mailboxes.Single().Address).IsEqualTo("no-reply@terrascale.tech");
        await Assert.That(accepted.Message.From.Mailboxes.Single().Name).IsEqualTo("TerraScale");
        await Assert.That(accepted.Message.ReplyTo.Count).IsEqualTo(0);
        var code = SixDigits().Match(accepted.Message.TextBody ?? "").Value;
        await Assert.That(code).IsNotEmpty();
        await Assert.That(cloudflare.Calls).IsEqualTo(0);

        using var verified = await oidc.PostFormAsync($"{SignUp}/verify", new() { ["email"] = email, ["code"] = code, ["pending_token"] = pending, ["next"] = string.Empty });
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(OidcHarness.SessionCookie(verified)).IsNotNull();
    }

    [Test]
    public async Task A_refusing_smtp_peer_fails_over_to_cloudflare_and_that_code_completes_the_sign_up()
    {
        await using var mail = await SmtpMailFixture.StartAsync();
        await using var cloudflare = await EmailProviderFixture.FakeCloudflare.StartAsync();
        await using var oidc = await StartAsync(fixture, mail, cloudflare);

        mail.RefuseRecipientsWith = SmtpServer.Protocol.SmtpReplyCode.MailboxUnavailable;
        var email = oidc.UniqueEmail("smtp-failover");
        using var started = await oidc.PostFormAsync(SignUp, new() { ["email"] = email, ["password"] = Password });
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(PendingToken().IsMatch(await started.Content.ReadAsStringAsync())).IsTrue();

        var body = await cloudflare.WaitForAsync(email);
        await Assert.That(cloudflare.Calls).IsEqualTo(1);
        await Assert.That(mail.Accepted.Count(message => message.To.Contains(email, StringComparer.Ordinal))).IsEqualTo(0);
        var request = JsonNode.Parse(body)!.AsObject();
        await Assert.That(request["to"]!.GetValue<string>()).IsEqualTo(email);
        await Assert.That(request["from"]!["address"]!.GetValue<string>()).IsEqualTo("no-reply@terrascale.tech");
        var code = SixDigits().Match(request["text"]!.GetValue<string>()).Value;
        await Assert.That(code).IsNotEmpty();
        var pending = PendingToken().Match(await started.Content.ReadAsStringAsync()).Groups[1].Value;

        using var verified = await oidc.PostFormAsync($"{SignUp}/verify", new() { ["email"] = email, ["code"] = code, ["pending_token"] = pending, ["next"] = string.Empty });
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(OidcHarness.SessionCookie(verified)).IsNotNull();
    }

    private static async Task<OidcHarness> StartAsync(TerraBaseFixture fixture, SmtpMailFixture mail, EmailProviderFixture.FakeCloudflare cloudflare, TimeProvider? clock = null) =>
        await OidcHarness.StartAsync(fixture, clock: clock, settings: settings =>
        {
            settings["smtp-host"] = "127.0.0.1";
            settings["smtp-port"] = mail.Endpoint.Port.ToString();
            settings["smtp-username"] = SmtpMailFixture.Username;
            settings["smtp-password"] = SmtpMailFixture.Password;
        }, services: services =>
        {
            services.AddSingleton(new SmtpEmailSender(
                new SmtpEmailOptions("127.0.0.1", mail.Endpoint.Port, SmtpMailFixture.Username, SmtpMailFixture.Password, RequireTls: false),
                NullLogger<SmtpEmailSender>.Instance));
            services.AddSingleton(new CloudflareEmailSender(
                new HttpClient(new EmailProviderFixture.ProviderRoute(cloudflare.Address, cloudflare) { InnerHandler = new HttpClientHandler() }),
                new CloudflareEmailOptions("synthetic-account", "synthetic-token"),
                NullLogger<CloudflareEmailSender>.Instance));
            services.AddSingleton(provider => new FailoverEmailSender(
                [provider.GetRequiredService<SmtpEmailSender>(), provider.GetRequiredService<CloudflareEmailSender>()],
                provider.GetRequiredService<ILogger<FailoverEmailSender>>()));
            services.AddSingleton<AdmittedEmailSender>();
            services.AddSingleton<IEmailSender>(provider => provider.GetRequiredService<AdmittedEmailSender>());
        });

    private sealed class RecordingSender(AdmittedEmailSender provider) : IEmailSender
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, TaskCompletionSource<EmailDeliveryOutcome>> outcomes = new(StringComparer.OrdinalIgnoreCase);

        public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) =>
            _ = await this.DeliverAsync(recipient, message, cancellationToken);

        public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        {
            var outcome = await provider.DeliverAsync(recipient, message, cancellationToken);
            lock (this.gate)
            {
                if (this.outcomes.TryGetValue(recipient, out var source))
                {
                    source.TrySetResult(outcome);
                }
                else
                {
                    var fresh = new TaskCompletionSource<EmailDeliveryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                    fresh.SetResult(outcome);
                    this.outcomes[recipient] = fresh;
                }
            }

            return outcome;
        }

        public Task<EmailDeliveryOutcome> OutcomeForAsync(string recipient)
        {
            lock (this.gate)
            {
                if (this.outcomes.TryGetValue(recipient, out var source))
                {
                    return source.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }

                var pending = new TaskCompletionSource<EmailDeliveryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                this.outcomes[recipient] = pending;
                return pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [GeneratedRegex(@"name=""pending_token"" value=""([^""]+)""")]
    private static partial Regex PendingToken();

    [GeneratedRegex(@"\b[0-9]{6}\b")]
    private static partial Regex SixDigits();
}
