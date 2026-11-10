using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Email;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class SmtpSignUpEmailTests(TerraBaseFixture fixture)
{
    private const string SignUp = $"/ui/{OidcIssuer.Slug}/sign-up";
    private const string Password = "A sound body keeps a sound mind!";

    [Test]
    public async Task A_sign_up_delivers_one_code_email_over_smtp_and_the_code_completes_the_sign_up()
    {
        await using var mail = await SmtpMailFixture.StartAsync();
        await using var oidc = await StartAsync(fixture, mail);

        var email = oidc.UniqueEmail("smtp-signup");
        using var started = await oidc.PostFormAsync(SignUp, new() { ["email"] = email, ["password"] = Password });
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var pending = PendingToken().Match(await started.Content.ReadAsStringAsync()).Groups[1].Value;
        await Assert.That(pending).IsNotEmpty();

        var accepted = await MailForAsync(mail, email);
        await Assert.That(mail.Accepted.Count(message => message.To.Contains(email, StringComparer.Ordinal))).IsEqualTo(1);
        await Assert.That(accepted.From).IsEqualTo("no-reply@terrascale.tech");
        await Assert.That(accepted.Message.From.Mailboxes.Single().Address).IsEqualTo("no-reply@terrascale.tech");
        await Assert.That(accepted.Message.From.Mailboxes.Single().Name).IsEqualTo("TerraScale");
        await Assert.That(accepted.Message.ReplyTo.Count).IsEqualTo(0);
        var code = SixDigits().Match(accepted.Message.TextBody ?? "").Value;
        await Assert.That(code).IsNotEmpty();

        using var verified = await oidc.PostFormAsync($"{SignUp}/verify", new() { ["email"] = email, ["code"] = code, ["pending_token"] = pending, ["next"] = string.Empty });
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(OidcHarness.SessionCookie(verified)).IsNotNull();
    }

    [Test]
    public async Task A_refusing_peer_leaves_the_sign_up_answer_unchanged_and_records_the_refusal()
    {
        await using var mail = await SmtpMailFixture.StartAsync();
        await using var oidc = await StartAsync(fixture, mail);
        var sender = oidc.Server.App.Services.GetRequiredService<RecordingSender>();

        mail.RefuseRecipientsWith = SmtpServer.Protocol.SmtpReplyCode.MailboxUnavailable;
        var rejected = oidc.UniqueEmail("smtp-rejected");
        using var refused = await oidc.PostFormAsync(SignUp, new() { ["email"] = rejected, ["password"] = Password });
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(PendingToken().IsMatch(await refused.Content.ReadAsStringAsync())).IsTrue();
        await Assert.That(await sender.OutcomeForAsync(rejected)).IsEqualTo(EmailDeliveryOutcome.Rejected);

        mail.RefuseRecipientsWith = SmtpServer.Protocol.SmtpReplyCode.Aborted;
        var retryable = oidc.UniqueEmail("smtp-retryable");
        using var temporary = await oidc.PostFormAsync(SignUp, new() { ["email"] = retryable, ["password"] = Password });
        await Assert.That(temporary.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(PendingToken().IsMatch(await temporary.Content.ReadAsStringAsync())).IsTrue();
        await Assert.That(await sender.OutcomeForAsync(retryable)).IsEqualTo(EmailDeliveryOutcome.Retryable);
    }

    private static async Task<OidcHarness> StartAsync(TerraBaseFixture fixture, SmtpMailFixture mail) =>
        await OidcHarness.StartAsync(fixture, settings: settings =>
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
            services.AddSingleton(provider => new RecordingSender(provider.GetRequiredService<AdmittedEmailSender>()));
            services.AddSingleton<IEmailSender>(provider => provider.GetRequiredService<RecordingSender>());
        });

    private static async Task<ReceivedMail> MailForAsync(SmtpMailFixture mail, string recipient)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var message in mail.Accepted)
            {
                if (message.To.Contains(recipient, StringComparer.Ordinal))
                {
                    return message;
                }
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("No SMTP message arrived for the sign-up address within 10 s.");
    }

    private sealed class RecordingSender(AdmittedEmailSender provider) : IEmailSender
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, EmailDeliveryOutcome> outcomes = new(StringComparer.OrdinalIgnoreCase);

        public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) =>
            _ = await this.DeliverAsync(recipient, message, cancellationToken);

        public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        {
            var outcome = await provider.DeliverAsync(recipient, message, cancellationToken);
            lock (this.gate)
            {
                this.outcomes[recipient] = outcome;
            }

            return outcome;
        }

        public async Task<EmailDeliveryOutcome?> OutcomeForAsync(string recipient)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                lock (this.gate)
                {
                    if (this.outcomes.TryGetValue(recipient, out var outcome))
                    {
                        return outcome;
                    }
                }

                await Task.Delay(50);
            }

            return null;
        }
    }

    [GeneratedRegex(@"name=""pending_token"" value=""([^""]+)""")]
    private static partial Regex PendingToken();

    [GeneratedRegex(@"\b[0-9]{6}\b")]
    private static partial Regex SixDigits();
}
