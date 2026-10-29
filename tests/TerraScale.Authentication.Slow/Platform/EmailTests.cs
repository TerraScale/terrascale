using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.EmailProviderFixture;

namespace TerraScale.Authentication.Slow.Platform;

/// <summary>
/// Transactional email: Cloudflare Email Sending is the sender when its account and token are configured, delivery
/// failures never fail the request, and neither logs nor the API answer ever carry the message, the recipient or the
/// token. The token travels only in the request's Authorization header.
/// </summary>
public sealed class EmailTests
{
    private const string Recipient = "private-person@example.test";
    private const string Subject = "Reset your password";
    private const string Body = "https://auth.test.invalid/reset?token=one-time-secret-8d2f";
    private const string Html = $"<p><a href=\"{Body}\">Reset your password</a></p>";
    private const string AccountId = "0123456789abcdef0123456789abcdef";
    private const string ApiToken = "cf-token-5c1e";
    private const string AcceptedResponse = "{\"success\":true,\"result\":{\"delivered\":[],\"queued\":[\"" + Recipient + "\"],\"permanent_bounces\":[],\"suppressed_recipients\":[]}}";

    [Test]
    public async Task Without_cloudflare_settings_mail_is_not_sent_and_only_sizes_and_a_safe_outcome_are_logged()
    {
        var logs = new EmailLogs();
        await using var server = await StartAsync(logs, cloudflare: null);

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await AssertOutcomeAsync(logs, "error");
        await Assert.That(logs.Text).Contains($"Email not delivered: Cloudflare Email Sending is not configured (recipient {Recipient.Length}, subject {Subject.Length}, text {Body.Length}, HTML {Html.Length} characters)");
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    public async Task Production_without_cloudflare_settings_refuses_to_start()
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values =>
        {
            values["environment"] = "Production";
            values["terrabase-url"] = "https://terrabase.example.invalid";
        }));

        await Assert.That(exception!.Message).Contains("CLOUDFLARE_ACCOUNT_ID").And.Contains("CLOUDFLARE_EMAIL_TOKEN");
    }

    [Test]
    public async Task A_delivery_posts_the_exact_message_with_the_account_token()
    {
        var logs = new EmailLogs();
        await using var cloudflare = await FakeCloudflare.StartAsync();
        await using var server = await StartAsync(logs, cloudflare);

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(cloudflare.RequestUrl).IsEqualTo($"https://api.cloudflare.com/client/v4/accounts/{AccountId}/email/sending/send");
        await Assert.That(cloudflare.Authorization).IsEqualTo($"Bearer {ApiToken}");
        using var sent = JsonDocument.Parse(cloudflare.Body!);
        var root = sent.RootElement;
        // The display name is the brand's; the address is always the fixed sender.
        await Assert.That(root.GetProperty("from").GetProperty("address").GetString()).IsEqualTo("no-reply@terrascale.tech");
        await Assert.That(root.GetProperty("from").GetProperty("name").GetString()).IsEqualTo("Acme \"Cloud\"");
        await Assert.That(root.GetProperty("to").GetString()).IsEqualTo(Recipient);
        await Assert.That(root.GetProperty("subject").GetString()).IsEqualTo(Subject);
        await Assert.That(root.GetProperty("text").GetString()).IsEqualTo(Body);
        await Assert.That(root.GetProperty("html").GetString()).IsEqualTo(Html);
        await Assert.That(root.GetProperty("reply_to").GetString()).IsEqualTo("help@example.test");
        await Assert.That(logs.Text).DoesNotContain("Email delivery failed");
        await AssertOutcomeAsync(logs, "queued");
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(nameof(EmailDeliveryOutcome.Accepted));
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    public async Task A_rejected_request_is_logged_by_status_only_and_does_not_fail_the_request()
    {
        var logs = new EmailLogs();
        await using var cloudflare = await FakeCloudflare.StartAsync(status: HttpStatusCode.Forbidden);
        await using var server = await StartAsync(logs, cloudflare);

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(logs.Text).Contains("Email delivery failed: Cloudflare answered 403");
        await AssertOutcomeAsync(logs, "error");
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    public async Task A_permanent_bounce_in_a_success_answer_is_logged_as_a_failed_delivery()
    {
        var logs = new EmailLogs();
        await using var cloudflare = await FakeCloudflare.StartAsync(
            body: $$$"""{"success":true,"result":{"delivered":[],"permanent_bounces":["{{{Recipient}}}"],"queued":[]}}""");
        await using var server = await StartAsync(logs, cloudflare);

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(logs.Text).Contains("Email delivery failed: Cloudflare reported a permanent bounce");
        await AssertOutcomeAsync(logs, "bounced");
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    public async Task An_unreachable_api_is_logged_and_does_not_fail_the_request()
    {
        var logs = new EmailLogs();
        await using var cloudflare = await FakeCloudflare.StartAsync(abort: true);
        await using var server = await StartAsync(logs, cloudflare);

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(logs.Text).Contains("Email delivery failed");
        await AssertOutcomeAsync(logs, "error");
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_canceled_send_propagates_cancellation_and_logs_a_safe_outcome(bool cloudflare)
    {
        var logs = new EmailLogs();
        await using var provider = await FakeCloudflare.StartAsync();
        await using var server = await StartAsync(logs, cloudflare ? provider : null);

        // The test route answers 409 when the sender throws OperationCanceledException.
        using var response = await server.Client.PostAsync("/__test/email/canceled", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(logs.Text).DoesNotContain("Email not delivered");
        await Assert.That(logs.Text).DoesNotContain("Email delivery failed");
        await AssertOutcomeAsync(logs, "error");
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    public async Task A_canceled_attempt_does_not_contact_the_provider()
    {
        var logs = new EmailLogs();
        await using var cloudflare = await FakeCloudflare.StartAsync(abort: true);
        await using var server = await StartAsync(logs, cloudflare);

        // Cancellation must prevent an attempt from reaching even a failing provider.
        using var response = await server.Client.PostAsync("/__test/email/canceled", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(cloudflare.Body).IsNull();
        await AssertOutcomeAsync(logs, "error");
    }

    [Test]
    public async Task A_failing_log_provider_does_not_fail_the_logging_sender()
    {
        await using var server = await StartAsync(new ThrowingLogs("LoggingEmailSender"), cloudflare: null);

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    }

    [Test]
    public async Task A_failing_log_provider_does_not_change_the_delivery_outcome()
    {
        await using var cloudflare = await FakeCloudflare.StartAsync();
        await using var server = await StartAsync(new ThrowingLogs("CloudflareEmailSender"), cloudflare);

        foreach (var (status, body, expected) in new (int Status, string Body, EmailDeliveryOutcome Expected)[]
        {
            (200, AcceptedResponse, EmailDeliveryOutcome.Accepted),
            (200, "{\"success\":true,\"result\":{\"permanent_bounces\":[\"suppressed\"]}}", EmailDeliveryOutcome.Rejected),
            (403, "{}", EmailDeliveryOutcome.Rejected),
            (429, "{}", EmailDeliveryOutcome.Retryable),
            (500, "{}", EmailDeliveryOutcome.Unknown),
        })
        {
            cloudflare.Status = (HttpStatusCode)status;
            cloudflare.ResponseBody = body;

            using var response = await server.Client.PostAsync("/__test/email", null);

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(expected.ToString());
        }
    }

    [Test]
    public async Task Provider_errors_have_one_safe_information_outcome()
    {
        var logs = new EmailLogs();
        await using var cloudflare = await FakeCloudflare.StartAsync();
        await using var server = await StartAsync(logs, cloudflare);

        foreach (var (status, body, outcome, expected) in new (int Status, string Body, string Outcome, EmailDeliveryOutcome Expected)[]
        {
            (429, "provider response containing secrets", "429", EmailDeliveryOutcome.Retryable),
            (500, "provider response containing secrets", "error", EmailDeliveryOutcome.Unknown),
            (200, "{\"success\":false}", "error", EmailDeliveryOutcome.Rejected),
            (200, "invalid JSON", "error", EmailDeliveryOutcome.Unknown),
            (200, "null", "error", EmailDeliveryOutcome.Unknown),
            (200, "[]", "error", EmailDeliveryOutcome.Unknown),
            (200, "{}", "error", EmailDeliveryOutcome.Unknown),
            (200, "{\"success\":true}", "error", EmailDeliveryOutcome.Unknown),
            (200, "{\"result\":{\"queued\":[\"private-person@example.test\"]}}", "error", EmailDeliveryOutcome.Unknown),
            (200, "{\"success\":null,\"result\":{\"queued\":[\"private-person@example.test\"]}}", "error", EmailDeliveryOutcome.Unknown),
            (200, "{\"success\":true,\"result\":null}", "error", EmailDeliveryOutcome.Unknown),
            (200, "{\"success\":true,\"result\":{\"queued\":[],\"delivered\":[]}}", "error", EmailDeliveryOutcome.Unknown),
            (200, "{\"success\":true,\"result\":{\"queued\":[\"someone-else@example.test\"]}}", "error", EmailDeliveryOutcome.Unknown),
            (200, "{\"success\":true,\"result\":{\"queued\":[null,\"private-person@example.test\"]}}", "error", EmailDeliveryOutcome.Unknown),
            (200, "{\"success\":true,\"result\":{\"queued\":[\"private-person@example.test\"],\"permanent_bounces\":[null]}}", "error", EmailDeliveryOutcome.Unknown),
            (200, "{\"success\":false,\"success\":true,\"result\":{\"queued\":[\"private-person@example.test\"]}}", "error", EmailDeliveryOutcome.Unknown),
            (200, "{\"success\":true,\"result\":{\"queued\":[\"private-person@example.test\"],\"suppressed_recipients\":[\"private-person@example.test\"]}}", "bounced", EmailDeliveryOutcome.Rejected),
            (200, "{\"success\":true,\"result\":{\"queued\":[\"private-person@example.test\"],\"suppressed_recipients\":null}}", "error", EmailDeliveryOutcome.Unknown),
        })
        {
            cloudflare.Status = (HttpStatusCode)status;
            cloudflare.ResponseBody = body;
            logs.Outcomes.Clear();

            using var response = await server.Client.PostAsync("/__test/email", null);

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(expected.ToString());
            await AssertOutcomeAsync(logs, outcome);
            await Assert.That(logs.Text).DoesNotContain(body);
            await AssertNoMessageDataAsync(logs.Text);
        }
    }

    [Test]
    public async Task A_delivered_recipient_is_also_explicit_provider_acceptance()
    {
        var logs = new EmailLogs();
        await using var cloudflare = await FakeCloudflare.StartAsync(body:
            $$$"""{"success":true,"result":{"delivered":["{{{Recipient}}}"],"queued":[],"permanent_bounces":[],"suppressed_recipients":[]}}""");
        await using var server = await StartAsync(logs, cloudflare);
        using var response = await server.Client.PostAsync("/__test/email", null);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(nameof(EmailDeliveryOutcome.Accepted));
        await AssertOutcomeAsync(logs, "queued");
        await AssertNoMessageDataAsync(logs.Text);
    }

    private static Task<AuthTestServer> StartAsync(ILoggerProvider logs, FakeCloudflare? cloudflare) => AuthTestServer.StartAsync(
        settings: values =>
        {
            if (cloudflare is not null)
            {
                values["cloudflare-account-id"] = AccountId;
                values["cloudflare-email-token"] = ApiToken;
            }
        },
        services: collection =>
        {
            collection.AddSingleton<ILoggerProvider>(logs);
            if (cloudflare is not null)
            {
                // Redirect only this client to the test-owned loopback provider.
                collection.AddHttpClient<IEmailSender, CloudflareEmailSender>().AddHttpMessageHandler(() => new ProviderRoute(cloudflare.Address, cloudflare));
            }
        },
        routes: app =>
        {
            app.MapPost("/__test/email", async (IEmailSender sender, CancellationToken cancellationToken) =>
            {
                var outcome = await sender.DeliverAsync(Recipient, new EmailMessage(Subject, Body, Html) { FromName = "Acme \"Cloud\"", ReplyTo = "help@example.test" }, cancellationToken);
                return Results.Text(outcome.ToString(), statusCode: StatusCodes.Status202Accepted);
            });
            app.MapPost("/__test/email/canceled", async (IEmailSender sender) =>
            {
                try
                {
                    await sender.SendAsync(Recipient, new EmailMessage(Subject, Body, Html), new CancellationToken(canceled: true));
                    return Results.Accepted();
                }
                catch (OperationCanceledException)
                {
                    return Results.StatusCode(StatusCodes.Status409Conflict);
                }
            });
        });

    private static async Task AssertNoMessageDataAsync(string text)
    {
        foreach (var secret in new[] { Recipient, Subject, Body, Html, "one-time-secret-8d2f", ApiToken, "help@example.test" })
        {
            await Assert.That(text).DoesNotContain(secret);
        }
    }

    private static async Task AssertOutcomeAsync(EmailLogs logs, string outcome)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Recipient)));
        await Assert.That(logs.Outcomes.Count).IsEqualTo(1);
        await Assert.That(logs.Outcomes.Single()).IsEqualTo($"Email send outcome: {outcome} (recipient SHA256 {digest})");
    }

    private sealed class EmailLogs : ILoggerProvider
    {
        private readonly CapturingLogs all = new();

        public ConcurrentQueue<string> Outcomes { get; } = new();

        public string Text => this.all.Text;

        public ILogger CreateLogger(string categoryName) => new Logger(this.all.CreateLogger(categoryName), this.Outcomes, categoryName);

        public void Dispose() => this.all.Dispose();

        private sealed class Logger(ILogger inner, ConcurrentQueue<string> outcomes, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                inner.Log(logLevel, eventId, state, exception, formatter);
                if (logLevel == LogLevel.Information && category.EndsWith("EmailSender", StringComparison.Ordinal))
                {
                    outcomes.Enqueue(formatter(state, exception));
                }
            }
        }
    }

    /// <summary>A log provider whose loggers for one category throw on every entry.</summary>
    private sealed class ThrowingLogs(string category) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName.Contains(category, StringComparison.Ordinal));

        public void Dispose()
        {
        }

        private sealed class Logger(bool throws) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (throws)
                {
                    throw new InvalidOperationException("log sink unavailable");
                }
            }
        }
    }

}
