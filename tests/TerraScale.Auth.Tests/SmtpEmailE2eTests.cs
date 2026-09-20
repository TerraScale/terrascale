namespace TerraScale.Auth.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Api;
using TerraBase.Integration;
using TerraBase.Security;
using TerraScale.DotNet;

/// <summary>
/// SMTP-delivered email end to end: a dedicated app boots with TS_AUTH_SMTP_* pointed at an
/// in-test SMTP server that performs a real STARTTLS upgrade (System.Net.Mail.SmtpClient with
/// EnableSsl=true refuses servers that do not advertise STARTTLS — verified empirically), and
/// the password-reset and organization-invitation flows are asserted on the wire: AUTH
/// credentials, envelope sender/recipients, and the full DATA payload.
/// </summary>
[NotInParallel]
public sealed class SmtpEmailE2eTests
{
    private const string OperatorKey = "operator-smtp-key";
    private const string SmtpUsername = "oci-user";
    private const string SmtpPassword = "oci-pass";
    private const string FromAddress = "noreply@terrascale.tech";

    // ---- Test 1: password-reset email delivered over SMTP --------------------------------

    [Test]
    public async Task PasswordResetEmailDeliversOverSmtp()
    {
        using var smtp = FakeSmtpServer.Start();
        await using var app = await BootAsync(smtp.Port);
        var (slug, apiKey) = await CreateInstanceAsync(app.Http, "smtp-reset-" + Unique());
        var email = $"reset-{Unique()}@example.com";
        await CreateUserAsync(app.Http, apiKey, slug, email);

        using var response = await app.Http.PostAsync($"/ui/{slug}/forgot-password",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var session = await smtp.WaitForSessionAsync(s => s.Data.Contains($"/ui/{slug}/reset-password"));
        await Assert.That(session.Username).IsEqualTo(SmtpUsername);
        await Assert.That(session.Password).IsEqualTo(SmtpPassword);
        await Assert.That(session.MailFrom).Contains(FromAddress);
        await Assert.That(session.RcptTo).Contains($"RCPT TO:<{email}>");
        await Assert.That(session.Data).Contains("Subject: Reset your password");
        await Assert.That(session.Data).Contains($"{app.Origin.ToString().TrimEnd('/')}/ui/{slug}/reset-password?token=");
    }

    // ---- Test 2: invitation email delivered over SMTP ------------------------------------

    [Test]
    public async Task InvitationEmailDeliversOverSmtp()
    {
        using var smtp = FakeSmtpServer.Start();
        await using var app = await BootAsync(smtp.Port);
        var (slug, apiKey) = await CreateInstanceAsync(app.Http, "smtp-invite-" + Unique());
        var invitee = $"invitee-{Unique()}@example.com";

        var organization = await PostJsonAsync(app.Http, apiKey, $"/v1/{slug}/organizations", new { name = "Acme Corp" });
        var orgId = organization.GetProperty("id").GetString()!;
        var invitation = await PostJsonAsync(app.Http, apiKey, $"/v1/{slug}/organizations/{orgId}/invitations",
            new { email = invitee, role = "admin" });
        await Assert.That(invitation.GetProperty("status").GetString()).IsEqualTo("pending");
        var invitationId = invitation.GetProperty("id").GetString()!;

        var session = await smtp.WaitForSessionAsync(s => s.Data.Contains($"/invitations/{invitationId}/accept"));
        await Assert.That(session.Username).IsEqualTo(SmtpUsername);
        await Assert.That(session.MailFrom).Contains(FromAddress);
        await Assert.That(session.RcptTo).Contains($"RCPT TO:<{invitee}>");
        await Assert.That(session.Data).Contains("Subject: You're invited to join Acme Corp");
        await Assert.That(session.Data).Contains($"{app.Origin.ToString().TrimEnd('/')}/ui/{slug}/invitations/{invitationId}/accept?token=");
    }

    // ---- Test 3: an SMTP outage never breaks the surrounding flow -------------------------

    [Test]
    public async Task SmtpOutageNeverBreaksTheFlow()
    {
        // A port with nothing listening: every connection is refused, like Email Delivery
        // being down. The sender must swallow it — the reset request still returns the
        // normal "check your email" page, not a 500.
        var deadPort = AuthServiceFixture.ReserveLoopbackPort();
        await using var app = await BootAsync(deadPort);
        var (slug, apiKey) = await CreateInstanceAsync(app.Http, "smtp-outage-" + Unique());
        var email = $"outage-{Unique()}@example.com";
        await CreateUserAsync(app.Http, apiKey, slug, email);

        using var response = await app.Http.PostAsync($"/ui/{slug}/forgot-password",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = await response.Content.ReadAsStringAsync();
        await Assert.That(page).Contains("Check your email");
    }

    // ---- Test 4: options validation (unit) ------------------------------------------------

    [Test]
    public async Task PartialSmtpConfigurationNamesMissingVariables()
    {
        // Any one of the four SMTP settings alone must fail at load naming every missing
        // variable — a half-configured deployment silently falling back to logged email
        // (or failing at the first send) is the failure this guards.
        foreach (var arg in new[]
        {
            "--smtp-host=smtp.example.com",
            "--smtp-username=oci-user",
            "--smtp-password=oci-pass",
            "--smtp-from-address=noreply@terrascale.tech",
        })
        {
            var error = Assert.Throws<InvalidOperationException>(() => TerraScale.Auth.Configuration.AuthOptions.Load(BaseArgs(arg)));
            await Assert.That(error.Message).Contains("TS_AUTH_SMTP_");
            // The setting present on this iteration is the one NOT named; the other three are.
            var present = arg[2..arg.IndexOf('=')];
            foreach (var variable in new[] { "TS_AUTH_SMTP_HOST", "TS_AUTH_SMTP_USERNAME", "TS_AUTH_SMTP_PASSWORD", "TS_AUTH_SMTP_FROM_ADDRESS" })
                if (variable["TS_AUTH_".Length..].ToLowerInvariant().Replace('_', '-') != present)
                    await Assert.That(error.Message).Contains(variable);
        }
    }

    [Test]
    public async Task CompleteSmtpConfigurationLoadsWithPortValidation()
    {
        var options = TerraScale.Auth.Configuration.AuthOptions.Load(BaseArgs(
            "--smtp-host=smtp.email.sa-saopaulo-1.oci.oraclecloud.com",
            "--smtp-username=ocid1.user.oc1..aaaa@example@oci",
            "--smtp-password=secret",
            "--smtp-from-address=noreply@terrascale.tech"));
        await Assert.That(options.SmtpHost).IsEqualTo("smtp.email.sa-saopaulo-1.oci.oraclecloud.com");
        await Assert.That(options.SmtpPort).IsEqualTo(587); // OCI STARTTLS default
        await Assert.That(options.SmtpUsername).IsEqualTo("ocid1.user.oc1..aaaa@example@oci");
        await Assert.That(options.SmtpPassword).IsEqualTo("secret");
        await Assert.That(options.SmtpFromAddress).IsEqualTo("noreply@terrascale.tech");

        var explicitPort = TerraScale.Auth.Configuration.AuthOptions.Load(BaseArgs(
            "--smtp-host=h", "--smtp-username=u", "--smtp-password=p", "--smtp-from-address=f@x", "--smtp-port=2525"));
        await Assert.That(explicitPort.SmtpPort).IsEqualTo(2525);

        foreach (var poison in new[] { "abc", "0", "70000" })
        {
            var error = Assert.Throws<InvalidOperationException>(() => TerraScale.Auth.Configuration.AuthOptions.Load(BaseArgs(
                "--smtp-host=h", "--smtp-username=u", "--smtp-password=p", "--smtp-from-address=f@x", $"--smtp-port={poison}")));
            await Assert.That(error.Message).Contains("TS_AUTH_SMTP_PORT");
        }
    }

    // ---- App boot: dedicated instance + engine, SMTP env pointed at the test server -------

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];

    private static string[] BaseArgs(params string[] extra)
    {
        var baseArgs = new List<string>
        {
            "--public-origin=https://example.com",
            "--terrascale-api-key=k",
            "--database=ts-auth",
            "--master-key=" + Convert.ToBase64String(new byte[32]),
            "--operator-key=op",
        };
        baseArgs.AddRange(extra);
        return [.. baseArgs];
    }

    private static async Task<SmtpApp> BootAsync(int smtpPort)
    {
        var database = "tsauth-smtp-" + Unique();
        var root = Path.Combine(Path.GetTempPath(), "ts-auth-smtp-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var engine = TerraBaseHost.CreateEmbedded(builder =>
        {
            builder.Services.AddSingleton<IStartupFilter, AuthServiceHooks.AuthenticatedPrincipal>();
            builder.Services.AddSingleton<ITenantAuthorizationHook, AuthServiceHooks.AllowAll>();
            builder.Configuration["TERRABASE_REGION"] = "GRU";
            builder.Configuration["TERRABASE_DATABASE_PATH"] = root;
            builder.Configuration["TERRABASE_ALLOW_SCHEMALESS"] = "true";
            builder.Configuration["TERRABASE_DATABASE_ROUTE"] = database;
            builder.Configuration["TERRABASE_DATABASE_TENANT"] = "tsauth-smtp-e2e";
            builder.Configuration["TERRABASE_DATABASE_ULID"] = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
            builder.Configuration["TERRABASE_DATABASE_MODE"] = "explicit";
            builder.Configuration["TERRABASE_DATABASE_HOME_REGION"] = "GRU";
            builder.Configuration["TERRABASE_DATABASE_SELECTED_REGIONS"] = "GRU";
        });
        await engine.Application.StartAsync();

        var engineClient = new TerraScaleClient(
            new HttpClient(engine.CreateHandler()) { BaseAddress = new Uri("http://127.0.0.1/") },
            new TerraScaleClientOptions { BaseAddress = new Uri("http://127.0.0.1/"), ApiKey = "smtp-e2e-key" });

        // Same all-or-nothing SMTP block a real deployment sets; only the host/port point
        // at the in-test server (or, for outage probes, at nothing).
        var environment = new Dictionary<string, string?>
        {
            ["TS_AUTH_TERRASCALE_API_KEY"] = "smtp-e2e-key",
            ["TS_AUTH_DATABASE"] = database,
            ["TS_AUTH_MASTER_KEY"] = Convert.ToBase64String(new byte[32]),
            ["TS_AUTH_AUDIT_SIGNING_KEY"] = Convert.ToBase64String(AuthServiceFixture.AuditSigningKey),
            ["TS_AUTH_AUDIT_VERIFY_KEYS"] = Convert.ToBase64String(AuthServiceFixture.AuditPreviousSigningKey),
            ["TS_AUTH_OPERATOR_KEY"] = OperatorKey,
            ["TS_AUTH_METRICS_KEY"] = "metrics-smtp-test-key",
            ["TS_AUTH_SMTP_HOST"] = "127.0.0.1",
            ["TS_AUTH_SMTP_PORT"] = smtpPort.ToString(),
            ["TS_AUTH_SMTP_USERNAME"] = SmtpUsername,
            ["TS_AUTH_SMTP_PASSWORD"] = SmtpPassword,
            ["TS_AUTH_SMTP_FROM_ADDRESS"] = FromAddress,
        };
        var (application, http, origin) = await AuthServiceFixture.StartAppAsync(environment, engineClient, _ => { });
        return new SmtpApp(http, origin, engine, application, root);
    }

    private static async Task<(string Slug, string ApiKey)> CreateInstanceAsync(HttpClient http, string slug)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/instances")
        {
            Content = JsonContent.Create(new { slug, name = "SMTP E2E" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OperatorKey);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("instance").GetProperty("slug").GetString()!, body.GetProperty("api_key").GetString()!);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient http, string apiKey, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static async Task CreateUserAsync(HttpClient http, string apiKey, string slug, string email)
    {
        await PostJsonAsync(http, apiKey, $"/v1/{slug}/users", new { email });
    }

    private sealed record SmtpApp(
        HttpClient Http, Uri Origin, EmbeddedTerraBaseHost Engine, WebApplication Application, string Root) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Application.StopAsync();
            await Application.DisposeAsync();
            await Engine.DisposeAsync();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>The engine-side startup hooks, mirroring AuthServiceFixture's private ones.</summary>
    private static class AuthServiceHooks
    {
        public sealed class AuthenticatedPrincipal : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
            {
                application.Use(async (context, continuation) =>
                {
                    context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "ts-auth-e2e")],
                        "ts-auth-e2e"));
                    await continuation().ConfigureAwait(false);
                });
                next(application);
            };
        }

        public sealed class AllowAll : ITenantAuthorizationHook
        {
            public ValueTask<TenantAuthorizationDecision> AuthorizeAsync(
                TenantAuthorizationRequest request, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(TenantAuthorizationDecision.AllowUntil(DateTimeOffset.UtcNow.AddSeconds(10)));
        }
    }

    // ---- The in-test SMTP server -----------------------------------------------------------

    /// <summary>
    /// A minimal ESMTP server on an ephemeral loopback port. Speaks just enough protocol for
    /// System.Net.Mail.SmtpClient: EHLO capabilities, STARTTLS (upgrades over a throwaway
    /// self-signed certificate — the sender keeps EnableSsl=true for OCI, and SmtpClient
    /// hard-fails with "Server does not support secure connections" when STARTTLS is not
    /// advertised, so a real upgrade is unavoidable), AUTH LOGIN and AUTH PLAIN, then the
    /// MAIL/RCPT/DATA transaction. Every completed session is captured for assertions.
    /// </summary>
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly X509Certificate2 certificate;
        private readonly CancellationTokenSource shuttingDown = new();

        public ConcurrentQueue<Session> Sessions { get; } = new();
        public int Port { get; }

        /// <summary>One captured SMTP conversation: the AUTH identity, the envelope, and the DATA payload.</summary>
        public sealed record Session(string? Username, string? Password, string? MailFrom, IReadOnlyList<string> RcptTo, string Data);

        static FakeSmtpServer()
        {
            // SmtpClient still consults ServicePointManager's callback (SocketsHttpHandler does
            // not, so this affects no other test's HTTP traffic): accept the throwaway cert.
#pragma warning disable SYSLIB0014
            ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore SYSLIB0014
        }

        private FakeSmtpServer(int port, X509Certificate2 certificate)
        {
            Port = port;
            this.certificate = certificate;
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(8);
        }

        public static FakeSmtpServer Start()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var server = new FakeSmtpServer(port, certificate);
            _ = Task.Run(server.AcceptLoopAsync);
            return server;
        }

        public async Task<Session> WaitForSessionAsync(Func<Session, bool> match)
        {
            // The HTTP response returns once the app's SendAsync completes; the server may
            // still be draining QUIT, so poll briefly instead of assuming synchronous capture.
            for (var attempt = 0; attempt < 150; attempt++)
            {
                var found = Sessions.ToArray().FirstOrDefault(match);
                if (found is not null)
                    return found;
                await Task.Delay(100);
            }
            throw new TimeoutException($"no SMTP session matched after 15s; captured: {Sessions.Count} session(s)" +
                string.Concat(Sessions.Select(s => $"\n  MAIL FROM={s.MailFrom} RCPT=[{string.Join(",", s.RcptTo)}] AUTH={s.Username} DATA:\n{s.Data}")));
        }

        public void Dispose()
        {
            shuttingDown.Cancel();
            listener.Stop();
        }

        private async Task AcceptLoopAsync()
        {
            while (!shuttingDown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(shuttingDown.Token);
                }
                catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    return;
                }
                _ = Task.Run(() => HandleConnectionAsync(client));
            }
        }

        private enum AuthStage { None, Username, Password }

        private async Task HandleConnectionAsync(TcpClient client)
        {
            using var _ = client;
            await using var stream = client.GetStream();
            // `output` follows the reader onto the SslStream after STARTTLS; `stream` stays
            // the disposal owner. Replying on the plain stream post-upgrade sends plaintext
            // the client parses as TLS records.
            Stream output = stream;
            var reader = new StreamReader(stream, Encoding.UTF8);
            await WriteAsync(output, "220 test ESMTP\r\n");

            var authStage = AuthStage.None;
            string? authUser = null, authPass = null, mailFrom = null;
            var rcptTo = new List<string>();
            var data = new StringBuilder();
            var inData = false;

            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (inData)
                    {
                        if (line == ".")
                        {
                            inData = false;
                            await WriteAsync(output, "250 ok\r\n");
                        }
                        else
                            data.AppendLine(line);
                        continue;
                    }

                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(output, "250-test\r\n250-STARTTLS\r\n250-AUTH LOGIN PLAIN\r\n250 OK\r\n");
                    }
                    else if (line.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(output, "220 go\r\n");
                        var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                        await tls.AuthenticateAsServerAsync(certificate);
                        output = tls;
                        reader = new StreamReader(tls, Encoding.UTF8);
                        // The client re-sends EHLO on the upgraded channel.
                    }
                    else if (line.StartsWith("AUTH LOGIN", StringComparison.OrdinalIgnoreCase))
                    {
                        var initial = line["AUTH LOGIN".Length..].Trim();
                        if (initial.Length > 0)
                        {
                            authUser = DecodeBase64(initial);
                            authStage = AuthStage.Password;
                            await WriteAsync(output, "334 " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Password:")) + "\r\n");
                        }
                        else
                        {
                            authStage = AuthStage.Username;
                            await WriteAsync(output, "334 " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Username:")) + "\r\n");
                        }
                    }
                    else if (line.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
                    {
                        var initial = line["AUTH PLAIN".Length..].Trim();
                        // authzid NUL authcid NUL password
                        var parts = Encoding.UTF8.GetString(Convert.FromBase64String(initial)).Split('\0');
                        authUser = parts.Length > 1 ? parts[^2] : parts[0];
                        authPass = parts[^1];
                        await WriteAsync(output, "235 ok\r\n");
                    }
                    else if (authStage == AuthStage.Username && line.Length > 0)
                    {
                        authUser = DecodeBase64(line);
                        authStage = AuthStage.Password;
                        await WriteAsync(output, "334 " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Password:")) + "\r\n");
                    }
                    else if (authStage == AuthStage.Password && line.Length > 0)
                    {
                        authPass = DecodeBase64(line);
                        authStage = AuthStage.None;
                        await WriteAsync(output, "235 ok\r\n");
                    }
                    else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase))
                    {
                        mailFrom = line;
                        await WriteAsync(output, "250 ok\r\n");
                    }
                    else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                    {
                        rcptTo.Add(line);
                        await WriteAsync(output, "250 ok\r\n");
                    }
                    else if (line == "DATA")
                    {
                        inData = true;
                        await WriteAsync(output, "354 go\r\n");
                    }
                    else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(output, "221 bye\r\n");
                        break;
                    }
                    else
                    {
                        // RSET, NOOP, and anything else the client might send.
                        await WriteAsync(output, "250 ok\r\n");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The client dropped the connection mid-conversation: keep what was captured.
            }

            if (mailFrom is not null || authUser is not null)
                Sessions.Enqueue(new Session(authUser, authPass, mailFrom, rcptTo, DecodeQuotedPrintable(data.ToString())));

        /// <summary>SmtpClient sends text bodies quoted-printable: soft line breaks every ~76
        /// chars, =XX hex escapes (=3D is '='). Assertions want the decoded link, not the wire form.</summary>
        static string DecodeQuotedPrintable(string encoded)
        {
            var unwrapped = encoded.Replace("=\n", "");
            return System.Text.RegularExpressions.Regex.Replace(unwrapped, "=([0-9A-F]{2})",
                match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
        }
        }

        private static string DecodeBase64(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch (FormatException)
            {
                return "";
            }
        }

        private static async Task WriteAsync(Stream stream, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
    }
}
