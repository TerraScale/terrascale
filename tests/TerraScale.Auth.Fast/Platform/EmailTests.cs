using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using TerraScale.Auth.Features.Email;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Platform;

/// <summary>
/// Transactional email: the configured sender is chosen from <c>TS_AUTH_SMTP_*</c>, delivery
/// failures never fail the request, and neither logs nor an untrusted relay see the message,
/// the recipient or the SMTP password unless TLS is established first.
/// </summary>
public sealed class EmailTests
{
    private const string Recipient = "private-person@example.test";
    private const string Subject = "Reset your password";
    private const string Body = "https://auth.test.invalid/reset?token=one-time-secret-8d2f";
    private const string Html = $"<p><a href=\"{Body}\">Reset your password</a></p>";
    private const string SmtpPassword = "smtp-password-5c1e";

    [Test]
    public async Task A_relay_with_a_stalled_tls_handshake_stops_on_disposal()
    {
        var relay = FakeRelay.Start(offerStartTls: true);
        using var client = new TcpClient();
        Task? shutdown = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(IPAddress.Loopback, relay.Port, deadline.Token);
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
            await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsEqualTo("220 relay.test ESMTP");
            await client.GetStream().WriteAsync("STARTTLS\r\n"u8.ToArray(), deadline.Token);
            await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsEqualTo("220 ready to start TLS");

            // Keep the accepted connection open without sending a TLS ClientHello.
            shutdown = relay.DisposeAsync().AsTask();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            // Even a failing regression releases the connection so it cannot hang the suite.
            client.Dispose();
            await (shutdown ?? relay.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static Task<AuthTestServer> StartAsync(ILoggerProvider logs, int? smtpPort, Action<Dictionary<string, string>>? settings = null, Action<IServiceCollection>? services = null) => AuthTestServer.StartAsync(
        settings: values =>
        {
            settings?.Invoke(values);
            if (smtpPort is { } port)
            {
                values["smtp-host"] = "127.0.0.1";
                values["smtp-port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                values["smtp-username"] = "relay-user";
                values["smtp-password"] = SmtpPassword;
                values["smtp-from-address"] = "no-reply@auth.test.invalid";
            }
        },
        services: collection =>
        {
            collection.AddSingleton<ILoggerProvider>(logs);
            services?.Invoke(collection);
        },
        routes: app =>
        {
            app.MapPost("/__test/email", async (IEmailSender sender, CancellationToken cancellationToken) =>
            {
                await sender.SendAsync(Recipient, new EmailMessage(Subject, Body, Html) { FromName = "Acme \"Cloud\"", ReplyTo = "help@example.test" }, cancellationToken);
                return Results.Accepted();
            });
            app.MapPost("/__test/email/canceled", async (IEmailSender sender, string? recipient) =>
            {
                try
                {
                    await sender.SendAsync(recipient ?? Recipient, new EmailMessage(Subject, Body, Html), new CancellationToken(canceled: true));
                    return Results.Accepted();
                }
                catch (OperationCanceledException)
                {
                    return Results.StatusCode(StatusCodes.Status409Conflict);
                }
            });
        });

    [Test]
    public async Task Without_smtp_settings_mail_is_not_sent_and_only_sizes_are_logged()
    {
        var logs = new CapturingLogs();
        await using var server = await StartAsync(logs, smtpPort: null);

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(logs.Text).Contains($"Email not delivered: no SMTP relay is configured (recipient {Recipient.Length}, subject {Subject.Length}, text {Body.Length}, HTML {Html.Length} characters)");
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    public async Task Production_without_smtp_settings_warns_at_startup()
    {
        var logs = new CapturingLogs();
        await using var server = await StartAsync(logs, smtpPort: null, settings: values =>
        {
            values.Remove("environment");
            values["terrascale-api-url"] = "https://api.terrascale.tech";
        });

        await Assert.That(logs.Text).Contains("TS_AUTH_SMTP_* is not configured: email is logged, not delivered");
    }

    [Test]
    public async Task A_relay_without_starttls_never_receives_credentials_or_the_message()
    {
        var logs = new CapturingLogs();
        await using var relay = FakeRelay.Start(offerStartTls: false);
        await using var server = await StartAsync(logs, relay.Port);

        using var response = await server.Client.PostAsync("/__test/email", null);
        var conversation = relay.Received;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(conversation.Any(line => line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))).IsTrue();
        foreach (var command in new[] { "AUTH", "MAIL", "RCPT", "DATA" })
        {
            await Assert.That(conversation.Any(line => line.StartsWith(command, StringComparison.OrdinalIgnoreCase))).IsFalse();
        }

        await Assert.That(string.Join('\n', conversation)).DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(SmtpPassword)));
        await Assert.That(logs.Text).Contains("SMTP delivery failed");
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    public async Task A_relay_with_an_untrusted_certificate_never_receives_credentials_or_the_message()
    {
        var logs = new CapturingLogs();
        await using var relay = FakeRelay.Start(offerStartTls: true);
        await using var server = await StartAsync(logs, relay.Port);

        using var response = await server.Client.PostAsync("/__test/email", null);
        var conversation = relay.Received;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        // The relay finishes its side of the handshake; the client then rejects the certificate
        // and never continues the session (no second EHLO, no AUTH) over that channel.
        await Assert.That(conversation).Contains("STARTTLS");
        await Assert.That(conversation.Count(line => line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))).IsEqualTo(1);
        foreach (var command in new[] { "AUTH", "MAIL", "RCPT", "DATA" })
        {
            await Assert.That(conversation.Any(line => line.StartsWith(command, StringComparison.OrdinalIgnoreCase))).IsFalse();
        }

        await Assert.That(logs.Text).Contains("SMTP delivery failed");
        await AssertNoMessageDataAsync(logs.Text);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_canceled_send_propagates_cancellation_and_logs_nothing(bool smtp)
    {
        var logs = new CapturingLogs();
        await using var server = await StartAsync(logs, smtp ? ClosedPort() : null);

        // The test route answers 409 when the sender throws OperationCanceledException.
        using var response = await server.Client.PostAsync("/__test/email/canceled", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(logs.Text).DoesNotContain("Email not delivered");
        await Assert.That(logs.Text).DoesNotContain("SMTP delivery failed");
    }

    [Test]
    public async Task A_failure_racing_caller_cancellation_still_reports_cancellation()
    {
        var logs = new CapturingLogs();
        await using var server = await StartAsync(logs, ClosedPort());

        // An unparsable recipient fails before any cancellation check, while the token is canceled.
        using var response = await server.Client.PostAsync("/__test/email/canceled?recipient=not%20an%20address", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task A_trusted_relay_receives_credentials_only_after_tls_and_the_exact_message()
    {
        var logs = new CapturingLogs();
        await using var relay = FakeRelay.Start(offerStartTls: true, acceptDelivery: true);
        // Like TerraBase's HttpClient in the store tests, the client trusts only this relay's certificate.
        SmtpClient? used = null;
        await using var server = await StartAsync(logs, relay.Port, services: services =>
            services.AddSingleton<Func<SmtpClient>>(() => used = new SmtpClient
            {
                ServerCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && certificate.GetRawCertData().AsSpan().SequenceEqual(relay.CertificateRawData),
            }));

        using var response = await server.Client.PostAsync("/__test/email", null);
        var conversation = relay.Received.ToList();
        var tls = conversation.IndexOf("(TLS handshake finished on the relay)");
        var auth = conversation.FindIndex(line => line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
        using var delivered = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(relay.Delivered!)));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(tls).IsGreaterThan(-1);
        await Assert.That(auth).IsGreaterThan(tls);
        await Assert.That(relay.Credentials).IsEqualTo(Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0relay-user\0{SmtpPassword}")));
        await Assert.That(conversation.Any(line => line.StartsWith("MAIL FROM:<no-reply@auth.test.invalid>", StringComparison.Ordinal))).IsTrue();
        await Assert.That(conversation.Any(line => line.StartsWith($"RCPT TO:<{Recipient}>", StringComparison.Ordinal))).IsTrue();
        await Assert.That(delivered.Subject).IsEqualTo(Subject);
        await Assert.That(delivered.Body?.ContentType.MimeType).IsEqualTo("multipart/alternative");
        await Assert.That(delivered.TextBody?.TrimEnd()).IsEqualTo(Body);
        await Assert.That(delivered.HtmlBody?.TrimEnd()).IsEqualTo(Html);
        // The display name is the brand's; the address is always the configured one.
        await Assert.That(delivered.From.Mailboxes.Single().Name).IsEqualTo("Acme \"Cloud\"");
        await Assert.That(delivered.From.Mailboxes.Single().Address).IsEqualTo("no-reply@auth.test.invalid");
        await Assert.That(delivered.ReplyTo.Mailboxes.Single().Address).IsEqualTo("help@example.test");
        await Assert.That(logs.Text).DoesNotContain("SMTP delivery failed");
        await AssertNoMessageDataAsync(logs.Text);
        await Assert.That(used!.CheckCertificateRevocation).IsTrue();
    }

    [Test]
    public async Task A_deployment_without_revocation_egress_turns_the_online_check_off()
    {
        await using var relay = FakeRelay.Start(offerStartTls: true, acceptDelivery: true);
        SmtpClient? used = null;
        await using var server = await StartAsync(new CapturingLogs(), relay.Port, settings: values => values["smtp-check-revocation"] = "false",
            services: services => services.AddSingleton<Func<SmtpClient>>(() => used = new SmtpClient
            {
                ServerCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && certificate.GetRawCertData().AsSpan().SequenceEqual(relay.CertificateRawData),
            }));

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(relay.Delivered).IsNotNull();
        await Assert.That(used!.CheckCertificateRevocation).IsFalse();
    }

    [Test]
    public async Task A_failing_log_provider_does_not_fail_the_logging_sender()
    {
        await using var server = await StartAsync(new ThrowingLogs("LoggingEmailSender"), smtpPort: null);

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    }

    [Test]
    public async Task A_failing_log_provider_does_not_turn_a_delivery_failure_into_a_failed_request()
    {
        await using var server = await StartAsync(new ThrowingLogs("SmtpEmailSender"), ClosedPort());

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    }

    [Test]
    public async Task An_unreachable_relay_is_logged_and_does_not_fail_the_request()
    {
        var logs = new CapturingLogs();
        await using var server = await StartAsync(logs, ClosedPort());

        using var response = await server.Client.PostAsync("/__test/email", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(logs.Text).Contains("SMTP delivery failed");
        await AssertNoMessageDataAsync(logs.Text);
    }

    private static int ClosedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task AssertNoMessageDataAsync(string text)
    {
        foreach (var secret in new[] { Recipient, Subject, Body, "one-time-secret-8d2f", SmtpPassword })
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
    /// An SMTP relay recording every command line. It offers AUTH and, optionally, STARTTLS with a
    /// self-signed certificate no default client trusts; once TLS is up it keeps recording, and with
    /// <c>acceptDelivery</c> it accepts AUTH PLAIN, MAIL, RCPT and DATA there.
    /// </summary>
    private sealed class FakeRelay : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentQueue<string> received = new();
        private readonly CancellationTokenSource stop = new();
        private readonly X509Certificate2 certificate = SelfSigned();
        private bool offerStartTls;
        private bool acceptDelivery;
        private Task? serving;

        public int Port => ((IPEndPoint)this.listener.LocalEndpoint).Port;

        public string[] Received => [.. this.received];

        public byte[] CertificateRawData => this.certificate.RawData;

        /// <summary>The AUTH PLAIN response, base64, as the client sent it over TLS.</summary>
        public string? Credentials { get; private set; }

        /// <summary>The DATA the relay accepted, dot-unstuffed.</summary>
        public string? Delivered { get; private set; }

        public static FakeRelay Start(bool offerStartTls, bool acceptDelivery = false)
        {
            var relay = new FakeRelay { offerStartTls = offerStartTls, acceptDelivery = acceptDelivery };
            relay.listener.Start();
            relay.serving = relay.ServeAsync();
            return relay;
        }

        public async ValueTask DisposeAsync()
        {
            await this.stop.CancelAsync();
            this.listener.Stop();
            try
            {
                await this.serving!;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
            {
                // Stopping the relay ends its accept loop.
            }

            this.stop.Dispose();
            this.certificate.Dispose();
        }

        private static X509Certificate2 SelfSigned()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        private async Task ServeAsync()
        {
            var certificate = this.certificate;
            while (!this.stop.IsCancellationRequested)
            {
                using var client = await this.listener.AcceptTcpClientAsync(this.stop.Token);
                Stream stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 relay.test ESMTP");
                try
                {
                    while (await reader.ReadLineAsync(this.stop.Token) is { } line)
                    {
                        this.received.Enqueue(line);
                        var delivering = this.acceptDelivery && stream is SslStream;
                        if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("250-relay.test");
                            await writer.WriteLineAsync(this.offerStartTls && stream is not SslStream ? "250-STARTTLS" : "250-8BITMIME");
                            await writer.WriteLineAsync(delivering ? "250 AUTH PLAIN" : "250 AUTH PLAIN LOGIN");
                        }
                        else if (delivering && line.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
                        {
                            var initial = line["AUTH PLAIN".Length..].Trim();
                            if (initial.Length == 0)
                            {
                                await writer.WriteLineAsync("334 ");
                                initial = await reader.ReadLineAsync(this.stop.Token) ?? string.Empty;
                            }

                            this.Credentials = initial;
                            await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                        }
                        else if (delivering && (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase)))
                        {
                            await writer.WriteLineAsync("250 2.1.0 OK");
                        }
                        else if (delivering && line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                            var data = new StringBuilder();
                            while (await reader.ReadLineAsync(this.stop.Token) is { } dataLine && dataLine != ".")
                            {
                                data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine).Append("\r\n");
                            }

                            this.Delivered = data.ToString();
                            await writer.WriteLineAsync("250 2.0.0 queued");
                        }
                        else if (line.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase) && this.offerStartTls)
                        {
                            await writer.WriteLineAsync("220 ready to start TLS");
                            var tls = new SslStream(stream);
                            try
                            {
                                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, this.stop.Token);
                            }
                            catch (Exception exception) when (exception is AuthenticationException or IOException or OperationCanceledException)
                            {
                                this.received.Enqueue("(TLS handshake failed)");
                                await tls.DisposeAsync();
                                break;
                            }

                            this.received.Enqueue("(TLS handshake finished on the relay)");
                            stream = tls;
                            reader = new StreamReader(stream, Encoding.ASCII);
                            writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                        }
                        else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("221 bye");
                            break;
                        }
                        else
                        {
                            await writer.WriteLineAsync("502 not implemented");
                        }
                    }
                }
                catch (IOException)
                {
                    // The client dropped the connection.
                }
                finally
                {
                    reader.Dispose();
                    await writer.DisposeAsync();
                }
            }
        }
    }
}
