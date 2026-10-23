using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Platform;

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

    [Test]
    public async Task Without_cloudflare_settings_mail_is_not_sent_and_only_sizes_are_logged()
    {
        var logs = new CapturingLogs();
        await using var server = await StartAsync(logs, cloudflare: null);

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(logs.Text).Contains($"Email not delivered: Cloudflare Email Sending is not configured (recipient {Recipient.Length}, subject {Subject.Length}, text {Body.Length}, HTML {Html.Length} characters)");
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    public async Task Production_without_cloudflare_settings_warns_at_startup()
    {
        var logs = new CapturingLogs();
        // Production needs storage; an unreachable TerraBase client is enough for a startup warning.
        await using var server = await StartAsync(logs, cloudflare: null, settings: values =>
        {
            values.Remove("environment");
        }, storage: true);

        await Assert.That(logs.Text).Contains("CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_EMAIL_TOKEN are not configured: email is logged, not delivered");
    }

    [Test]
    public async Task A_delivery_posts_the_exact_message_with_the_account_token()
    {
        var logs = new CapturingLogs();
        var cloudflare = new FakeCloudflare();
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
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    public async Task A_rejected_request_is_logged_by_status_only_and_does_not_fail_the_request()
    {
        var logs = new CapturingLogs();
        await using var server = await StartAsync(logs, new FakeCloudflare(status: HttpStatusCode.Forbidden));

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(logs.Text).Contains("Email delivery failed: Cloudflare answered 403");
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    public async Task A_permanent_bounce_in_a_success_answer_is_logged_as_a_failed_delivery()
    {
        var logs = new CapturingLogs();
        await using var server = await StartAsync(logs, new FakeCloudflare(
            body: $$$"""{"success":true,"result":{"delivered":[],"permanent_bounces":["{{{Recipient}}}"],"queued":[]}}"""));

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(logs.Text).Contains("Email delivery failed: Cloudflare reported a permanent bounce");
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    public async Task An_unreachable_api_is_logged_and_does_not_fail_the_request()
    {
        var logs = new CapturingLogs();
        await using var server = await StartAsync(logs, new FakeCloudflare(failure: new HttpRequestException("connection refused")));

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(logs.Text).Contains("Email delivery failed");
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_canceled_send_propagates_cancellation_and_logs_nothing(bool cloudflare)
    {
        var logs = new CapturingLogs();
        await using var server = await StartAsync(logs, cloudflare ? new FakeCloudflare() : null);

        // The test route answers 409 when the sender throws OperationCanceledException.
        using var response = await server.Client.PostAsync("/__test/email/canceled", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(logs.Text).DoesNotContain("Email not delivered");
        await Assert.That(logs.Text).DoesNotContain("Email delivery failed");
    }

    [Test]
    public async Task A_failure_racing_caller_cancellation_still_reports_cancellation()
    {
        var logs = new CapturingLogs();
        await using var server = await StartAsync(logs, new FakeCloudflare(failure: new HttpRequestException("connection reset")));

        // The transport fails while the caller's token is already canceled.
        using var response = await server.Client.PostAsync("/__test/email/canceled", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task A_failing_log_provider_does_not_fail_the_logging_sender()
    {
        await using var server = await StartAsync(new ThrowingLogs("LoggingEmailSender"), cloudflare: null);

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    }

    [Test]
    public async Task A_failing_log_provider_does_not_turn_a_delivery_failure_into_a_failed_request()
    {
        await using var server = await StartAsync(new ThrowingLogs("CloudflareEmailSender"), new FakeCloudflare(status: HttpStatusCode.InternalServerError));

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    }

    private static Task<AuthTestServer> StartAsync(ILoggerProvider logs, FakeCloudflare? cloudflare, Action<Dictionary<string, string>>? settings = null, bool storage = false) => AuthTestServer.StartAsync(
        settings: values =>
        {
            settings?.Invoke(values);
            if (cloudflare is not null)
            {
                values["cloudflare-account-id"] = AccountId;
                values["cloudflare-email-token"] = ApiToken;
            }
        },
        services: collection =>
        {
            collection.AddSingleton<ILoggerProvider>(logs);
            if (storage)
            {
                collection.AddSingleton(new TerraBaseDocumentsClient(new HttpClient(), new TerraBaseDocumentsClientOptions { BaseAddress = new Uri("http://127.0.0.1:9/") })
                    .Database(App.StorageProject, App.StorageDatabase));
            }

            if (cloudflare is not null)
            {
                // The test replaces only the transport; the registration itself is App's.
                collection.AddHttpClient<IEmailSender, CloudflareEmailSender>().ConfigurePrimaryHttpMessageHandler(() => cloudflare);
            }
        },
        routes: app =>
        {
            app.MapPost("/__test/email", async (IEmailSender sender, CancellationToken cancellationToken) =>
            {
                await sender.SendAsync(Recipient, new EmailMessage(Subject, Body, Html) { FromName = "Acme \"Cloud\"", ReplyTo = "help@example.test" }, cancellationToken);
                return Results.Accepted();
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
        foreach (var secret in new[] { Recipient, Subject, Body, "one-time-secret-8d2f", ApiToken })
        {
            await Assert.That(text).DoesNotContain(secret);
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

    /// <summary>
    /// The Cloudflare API as a message handler. It records the request it receives and answers with
    /// <paramref name="status"/>, or fails with <paramref name="failure"/> the way an unreachable or reset connection does.
    /// </summary>
    private sealed class FakeCloudflare(HttpStatusCode status = HttpStatusCode.OK, Exception? failure = null, string body = "{\"success\":true}") : HttpMessageHandler
    {
        public string? RequestUrl { get; private set; }

        public string? Authorization { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                throw failure;
            }

            cancellationToken.ThrowIfCancellationRequested();
            this.RequestUrl = request.RequestUri?.ToString();
            this.Authorization = request.Headers.Authorization?.ToString();
            this.Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
