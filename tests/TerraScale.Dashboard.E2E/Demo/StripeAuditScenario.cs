using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Stripe;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E;

// The CLI child uses Stripe.net through an actual CONNECT/TLS boundary. Only
// this test binary trusts the ephemeral CA; no production endpoint is changed.
public sealed class StripeAuditScenario : IAsyncDisposable
{
    public sealed record Request(string Method, string Target, IReadOnlyDictionary<string, string> Headers);
    public sealed record Result(int ExitCode, string Output, string Error);
    private readonly string root;
    private readonly string mode;
    private readonly string fault;
    private readonly bool restricted;
    private readonly string temporaryDirectory;
    private readonly X509Certificate2 certificate;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentBag<TcpClient> clients = [];
    private readonly ConcurrentBag<Task> connections = [];
    private readonly ConcurrentQueue<Request> requests = [];
    private readonly ConcurrentQueue<string> priceFacts = [];
    private readonly ConcurrentQueue<string> failures = [];
    private readonly Task accepting;
    public string ApiVersion => StripeConfiguration.ApiVersion;
    public string Secret => $"{(restricted ? "rk" : "sk")}_{mode}_STRIPE_AUDIT_PRIVATE";
    public IReadOnlyList<Request> Requests => requests.ToArray();
    public IReadOnlyList<string> PriceFacts => priceFacts.ToArray();
    public IReadOnlyList<string> Failures => failures.ToArray();
    public int Connects;

    private static readonly string[] Events =
    [
        "checkout.session.completed", "checkout.session.async_payment_succeeded", "checkout.session.async_payment_failed",
        "checkout.session.expired", "customer.subscription.created", "customer.subscription.updated", "customer.subscription.deleted",
        "customer.subscription.paused", "customer.subscription.resumed", "customer.subscription.pending_update_applied",
        "customer.subscription.pending_update_expired", "customer.subscription.trial_will_end", "invoice.finalized", "invoice.paid",
        "invoice.payment_succeeded", "invoice.payment_failed", "invoice.payment_action_required", "invoice.marked_uncollectible", "invoice.voided"
    ];

    public StripeAuditScenario(string root, string mode = "live", string fault = "", bool restricted = false)
    {
        this.root = root; this.mode = mode; this.fault = fault; this.restricted = restricted;
        temporaryDirectory = Path.Combine(Path.GetTempPath(), "dashboard-stripe-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        using var authorityKey = RSA.Create(2048);
        var authorityRequest = new CertificateRequest("CN=Dashboard Stripe audit E2E CA", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var authorityIdentifier = new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, false);
        authorityRequest.CertificateExtensions.Add(authorityIdentifier);
        authorityRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(authorityIdentifier));
        using var authority = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        System.IO.File.WriteAllText(CaPath, authority.ExportCertificatePem());
        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=api.stripe.com", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        serverRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(serverRequest.PublicKey, false));
        serverRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("api.stripe.com");
        serverRequest.CertificateExtensions.Add(names.Build());
        using var publicCertificate = serverRequest.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(30), RandomNumberGenerator.GetBytes(16));
        certificate = publicCertificate.CopyWithPrivateKey(serverKey);
        listener.Start();
        accepting = AcceptAsync();
    }

    private string CaPath => Path.Combine(temporaryDirectory, "authority.pem");
    private string Proxy => "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;

    public DashboardCredentials Identity(string? key = null) => new("test-dashboard-client-secret-0123456789abcdef",
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), new(key ?? Secret, "whsec_STRIPE_AUDIT_PRIVATE"), TestIdentity.PeerSecret);

    public async Task<Result> RunAsync(string[]? arguments = null, DashboardCredentials? identity = null)
    {
        var start = new ProcessStartInfo("dotnet")
        { WorkingDirectory = root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(StripeAuditScenario).Assembly.Location);
        start.ArgumentList.Add("--stripe-audit-test-host"); start.ArgumentList.Add(Proxy); start.ArgumentList.Add(CaPath);
        foreach (var argument in arguments ?? []) start.ArgumentList.Add(argument);
        var proxy = Proxy;
        foreach (var name in start.Environment.Keys.Where(name => name.Contains("proxy", StringComparison.OrdinalIgnoreCase) || name.StartsWith("STRIPE_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        using var process = Process.Start(start) ?? throw new IOException("Could not launch the Stripe audit E2E process.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            var document = JsonSerializer.Serialize(identity ?? Identity());
            await process.StandardInput.WriteAsync(document.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token);
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stopping.Token);
                clients.Add(client); connections.Add(ServeAsync(client));
            }
        }
        catch (Exception exception) when (stopping.IsCancellationRequested && exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var tcp = client.GetStream();
                var tunnel = await HeadersAsync(tcp, deadline.Token);
                if (!tunnel.StartsWith("CONNECT api.stripe.com:443 HTTP/1.", StringComparison.Ordinal))
                    throw new InvalidDataException("The audit proxy accepts only the Stripe TLS endpoint.");
                Interlocked.Increment(ref Connects);
                await tcp.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), deadline.Token);
                using var tls = new SslStream(tcp, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
                var headers = (await HeadersAsync(tls, deadline.Token)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var line = headers[0].Split(' ');
                if (line.Length != 3) throw new InvalidDataException("The audit producer requires an HTTP request line.");
                var fields = headers.Skip(1).Select(header => header.Split(':', 2)).ToDictionary(header => header[0], header => header[1].Trim(), StringComparer.OrdinalIgnoreCase);
                requests.Enqueue(new(line[0], line[1], fields));
                if (line[0] != "GET" || fields.GetValueOrDefault("Host") != "api.stripe.com" || fields.GetValueOrDefault("Authorization") != "Bearer " + Secret || fields.GetValueOrDefault("Stripe-Version") != ApiVersion)
                    throw new InvalidDataException("The audit producer rejected a method, authority, credential or Stripe API version.");
                var target = new Uri("https://api.stripe.com" + line[1]);
                var body = Body(target);
                var status = fault == "http-error" ? "500 Internal Server Error" : "200 OK";
                if (fault == "http-error") body = "{\"error\":\"sk_live_STRIPE_AUDIT_PRIVATE\"}";
                if (fault == "malformed-json") body = "not-json sk_live_STRIPE_AUDIT_PRIVATE";
                var bytes = Encoding.UTF8.GetBytes(body);
                await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), deadline.Token);
                await tls.WriteAsync(bytes, deadline.Token);
            }
            catch (Exception exception) when (stopping.IsCancellationRequested && exception is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
            catch (Exception exception) { failures.Enqueue(exception.GetType().Name); }
        }
    }

    private string Body(Uri target)
    {
        var live = mode == "live";
        var path = target.AbsolutePath;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(target.Query);
        if (path == "/v1/prices")
        {
            if (query.GetValueOrDefault("limit") != "100" || query.GetValueOrDefault("active") != "true" ||
                query.GetValueOrDefault("currency") != "usd" || query.GetValueOrDefault("type") != "recurring" ||
                query.GetValueOrDefault("expand[0]") != "data.product")
                throw new InvalidDataException("Price discovery must use the official bounded recurring-price list with product expansion.");
            var all = new List<(string Plan, string Interval, int Amount, string Id, bool Active, bool ProductActive)>
            {
                ("starter", "annual", 3000, "price_starter_annual", true, true),
                ("starter", "monthly", 300, "price_starter_monthly", true, true),
                ("pro", "monthly", 2000, "price_pro_monthly", true, true),
                ("pro", "annual", 20000, "price_pro_annual", true, true),
                ("team", "monthly", 5000, "price_team_monthly", true, true),
                ("team", "annual", 50000, "price_team_annual", true, true)
            };
            if (fault == "missing-price") all.RemoveAll(row => row.Id == "price_starter_annual");
            if (fault == "duplicate-price") all.Add(("pro", "monthly", 2000, "price_pro_monthly_duplicate", true, true));
            if (fault == "inactive-price")
            {
                var row = all[0]; all[0] = (row.Plan, row.Interval, row.Amount, row.Id, false, row.ProductActive);
            }
            if (fault == "inactive-product")
            {
                var row = all[0]; all[0] = (row.Plan, row.Interval, row.Amount, row.Id, row.Active, false);
            }
            var after = query.GetValueOrDefault("starting_after").ToString();
            var start = after.Length == 0 ? 0 : all.FindIndex(value => value.Id == after) + 1;
            if (after.Length > 0 && start == 0) throw new InvalidDataException("Unknown price cursor.");
            var rows = all.Skip(start).Take(2).ToArray();
            foreach (var row in rows) priceFacts.Enqueue($"{row.Id}|{row.Plan}|{row.Interval}|{row.Amount}|{row.Active}|{row.ProductActive}");
            return JsonSerializer.Serialize(new { @object = "list", url = "/v1/prices", has_more = start + rows.Length < all.Count, data = rows.Select(row => Price(row.Plan, row.Interval, row.Amount, row.Plan == "team", row.Active, row.Id, row.ProductActive)) });
        }
        if (path.StartsWith("/v1/products/", StringComparison.Ordinal) && target.Query.Length == 0)
        {
            var key = path["/v1/products/".Length..].Replace("prod_", "", StringComparison.Ordinal);
            var plan = key.Split('_')[0];
            return JsonSerializer.Serialize(Product(plan, fault == "product-mode" ? !live : live, path["/v1/products/".Length..]));
        }
        if (path == "/v1/webhook_endpoints")
        {
            if (query.GetValueOrDefault("limit") != "100") throw new InvalidDataException("Webhook inventory must use the bounded SDK page size.");
            var paths = fault switch
            {
                "duplicate-webhooks" => new[] { "/api/v1/billing/stripe/webhook", "/api/auth/stripe/webhook" },
                "wrong-webhook" => ["/api/v1/billing/stripe/webhook/other"],
                _ => ["/api/v1/billing/stripe/webhook"]
            };
            return JsonSerializer.Serialize(new { @object = "list", url = "/v1/webhook_endpoints", has_more = false, data = paths.Select(route => new { id = "we_test", @object = "webhook_endpoint", url = "https://dash.terrascale.tech" + route, livemode = fault == "webhook-mode" ? !live : live, status = "enabled", api_version = ApiVersion, enabled_events = Events }) });
        }
        if (path == "/v1/events")
        {
            if (query.GetValueOrDefault("type") != "invoice.created" || query.GetValueOrDefault("limit") != "10")
                throw new InvalidDataException("Delivery inventory must request recent invoice.created events with its bounded page size.");
            return JsonSerializer.Serialize(new { @object = "list", url = "/v1/events", has_more = false, data = new[] { new { id = "evt_test", @object = "event", type = "invoice.created", livemode = fault == "event-mode" ? !live : live, pending_webhooks = 1 } } });
        }
        if (path == "/v1/billing_portal/configurations")
        {
            if (query.GetValueOrDefault("limit") != "100") throw new InvalidDataException("Portal inventory must use the bounded SDK page size.");
            object Portal(bool isDefault, bool switching) => new
            {
                id = isDefault ? "bpc_default" : "bpc_other",
                @object = "billing_portal.configuration",
                is_default = isDefault,
                active = true,
                livemode = fault == "portal-mode" ? !live : live,
                features = new { payment_method_update = new { enabled = true }, invoice_history = new { enabled = true }, subscription_cancel = new { enabled = true }, subscription_update = new { enabled = switching }, customer_update = new { enabled = true, allowed_updates = fault == "portal-tax-fields" ? new[] { "name", "address" } : new[] { "name", "address", "tax_id" } } }
            };
            var rows = fault == "unsafe-default-portal" ? new[] { Portal(false, false), Portal(true, true) } : [Portal(true, false)];
            return JsonSerializer.Serialize(new { @object = "list", url = "/v1/billing_portal/configurations", has_more = false, data = rows });
        }
        if (path == "/v1/subscriptions")
        {
            if (query.GetValueOrDefault("limit") != "100" || query.GetValueOrDefault("status") != "all")
                throw new InvalidDataException("Subscription inventory must request all statuses with the bounded SDK page size.");
            var after = query.GetValueOrDefault("starting_after").ToString();
            var rows = after.Length == 0
                ? new[] { Subscription("sub_first", "active", "cus_PRIVATE", live), Subscription("sub_second", "past_due", "cus_PRIVATE", live) }
                : new[] { Subscription("sub_third", "trialing", "cus_other_PRIVATE", live), Subscription("sub_fourth", "canceled", "cus_other_PRIVATE", live) };
            return JsonSerializer.Serialize(new { @object = "list", url = "/v1/subscriptions", has_more = after.Length == 0, data = rows });
        }
        throw new InvalidDataException("Unexpected Stripe audit path.");
    }

    private object Price(string plan, string interval, int amount, bool unexpanded, bool active, string id, bool productActive) => new
    {
        id,
        @object = "price",
        active,
        livemode = fault == "price-mode" && plan == "starter" && interval == "annual" ? mode != "live" : mode == "live",
        currency = "usd",
        unit_amount = amount,
        type = "recurring",
        recurring = new { interval = interval == "monthly" ? "month" : "year", interval_count = 1 },
        product = unexpanded ? "prod_team_monthly" : Product(plan, fault == "product-mode" ? mode != "live" : mode == "live", active: productActive)
    };
    private static object Product(string plan, bool live, string? id = null, bool active = true) => new { id = id ?? "prod_" + plan, @object = "product", active, livemode = live, name = plan, metadata = new { plan } };
    private object Subscription(string id, string status, string customer, bool live) => new { id, @object = "subscription", status, customer, livemode = fault == "subscription-mode" ? !live : live };
    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static async Task<string> HeadersAsync(Stream stream, CancellationToken cancellation)
    {
        using var buffer = new MemoryStream();
        var next = new byte[1]; var ending = 0;
        while (buffer.Length < 32768)
        {
            if (await stream.ReadAsync(next, cancellation) == 0) throw new IOException("Incomplete HTTP headers.");
            buffer.WriteByte(next[0]);
            ending = next[0] == (ending % 2 == 0 ? '\r' : '\n') ? ending + 1 : next[0] == '\r' ? 1 : 0;
            if (ending == 4) return Encoding.ASCII.GetString(buffer.ToArray());
        }
        throw new InvalidDataException("The fixture refuses oversized HTTP headers.");
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync(); listener.Stop();
        foreach (var client in clients) client.Dispose();
        try { await accepting; } catch (SocketException) { }
        try { await Task.WhenAll(connections); } catch (OperationCanceledException) { }
        certificate.Dispose(); stopping.Dispose();
        try { Directory.Delete(temporaryDirectory, recursive: true); } catch (IOException) { }
    }
}
