using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.RateLimiting;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Auth.Fast.Platform;

public sealed class ConfigurationTests
{
    private const string TrustedProxiesError = "TS_AUTH_TRUSTED_PROXIES must contain unadorned numeric IP addresses";
    private const string UnspecifiedProxiesError = "TS_AUTH_TRUSTED_PROXIES must not contain unspecified addresses";
    private const string DuplicateProxiesError = "TS_AUTH_TRUSTED_PROXIES must contain distinct normalized addresses";
    private const string TooManyProxiesError = "TS_AUTH_TRUSTED_PROXIES must contain at most 8 addresses";

    [Test]
    [Arguments("master-key", "TS_AUTH_MASTER_KEY is required")]
    [Arguments("operator-key", "TS_AUTH_OPERATOR_KEY is required")]
    [Arguments("public-origin", "TS_AUTH_PUBLIC_ORIGIN is required")]
    [Arguments("database", "TS_AUTH_DATABASE is required")]
    [Arguments("terrascale-api-url", "TS_AUTH_TERRASCALE_API_URL is required")]
    public async Task Startup_refuses_missing_required_settings(string key, string message)
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values => values.Remove(key)));

        await Assert.That(exception!.Message).IsEqualTo(message);
    }

    [Test]
    public async Task Production_requires_an_https_public_origin()
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values =>
        {
            values.Remove("environment");
            values["terrascale-api-url"] = "https://api.terrascale.tech";
            values["public-origin"] = "http://auth.example.invalid";
        }));

        await Assert.That(exception!.Message).IsEqualTo("TS_AUTH_PUBLIC_ORIGIN must use HTTPS outside Development");
    }

    [Test]
    [Arguments("not a dsn secretvalue")]
    [Arguments("https://glitchtip.example/secretvalue")]
    [Arguments("ftp://secretvalue@glitchtip.example/3")]
    [Arguments("https://:secretvalue@glitchtip.example/3")]
    public async Task The_service_binary_rejects_a_malformed_dsn_without_echoing_it(string dsn)
    {
        var settings = AuthTestServer.DefaultSettings();
        settings["sentry-dsn"] = dsn;
        var (exitCode, output) = await RunBinaryAsync("--rate-limit=1000", settings);

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(output).Contains("TS_AUTH_SENTRY_DSN must be a valid DSN");
        await Assert.That(output).DoesNotContain("secretvalue");
        await Assert.That(output).DoesNotContain("   at ");
    }

    [Test]
    public async Task Production_requires_an_audit_signing_key()
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values =>
        {
            values.Remove("environment");
            values.Remove("audit-signing-key");
            values["terrascale-api-url"] = "https://api.terrascale.tech";
        }));

        await Assert.That(exception!.Message).IsEqualTo("TS_AUTH_AUDIT_SIGNING_KEY is required outside Development");
    }

    [Test]
    public async Task Production_requires_the_signing_key_vault()
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values =>
        {
            values.Remove("environment");
            values["terrascale-api-url"] = "https://api.terrascale.tech";
        }));

        await Assert.That(exception!.Message).IsEqualTo("TS_AUTH_SIGNING_KEY_VAULT and its TS_AUTH_OCI_* credential are required outside Development");
    }

    [Test]
    [Arguments("ocid1.vault.oc1.sa-saopaulo-1.ffvmfu2naagt6.abtxeljr7ebplea4ua3memgthsupcfn3crg6d56vipsw7o2butchdrx3urmq")]
    [Arguments("ocid1.vault.oc1.sa-saopaulo-1.abtxeljr7ebplea4ua3memgthsupcfn3crg6d56vipsw7o2butchdrx3urmq")]
    public async Task Realistic_vault_ocids_with_or_without_the_future_use_part_are_accepted(string vault)
    {
        var values = AuthTestServer.DefaultSettings();
        FakeOciVault.For(Guid.NewGuid().ToString()).Bind(values);
        values["signing-key-vault"] = vault;
        values["oci-tenancy"] = "ocid1.tenancy.oc1..aaaaaaaaypdff62jbchkojfli5i22ybzm3qnptwwsbg3aa2t5ali6te52nzq";
        values["oci-user"] = "ocid1.user.oc1..aaaaaaaa3jtbxsw3sxb2ilkntp2rzqxm6a7kbp2oyrqqqpicnqyzshtd5lca";

        var options = AuthOptions.Load(values);

        await Assert.That(options.SigningKeyVault!.VaultId).IsEqualTo(vault);
    }

    [Test]
    public async Task Vaults_outside_the_commercial_realm_are_refused_explicitly()
    {
        var values = AuthTestServer.DefaultSettings();
        FakeOciVault.For(Guid.NewGuid().ToString()).Bind(values);
        values["oci-region"] = "us-gov-ashburn-1";
        values["signing-key-vault"] = "ocid1.vault.oc2.us-gov-ashburn-1.bbbtest.vaulttest";

        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => Task.FromResult(AuthOptions.Load(values)));

        await Assert.That(exception!.Message).IsEqualTo("TS_AUTH_SIGNING_KEY_VAULT must be a vault OCID in the commercial OCI realm (oc1)");
    }

    [Test]
    public async Task An_oci_endpoint_alone_is_a_partial_vault_configuration()
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values => values["oci-endpoint"] = "https://127.0.0.1:9"));

        await Assert.That(exception!.Message).IsEqualTo("TS_AUTH_SIGNING_KEY_VAULT, TS_AUTH_SIGNING_KEY_SECRET and TS_AUTH_OCI_* are partially configured");
    }

    [Test]
    public async Task Production_refuses_a_plain_http_oci_endpoint()
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values =>
        {
            values.Remove("environment");
            values["terrascale-api-url"] = "https://api.terrascale.tech";
            FakeOciVault.For(Guid.NewGuid().ToString()).Bind(values);
        }));

        await Assert.That(exception!.Message).IsEqualTo("TS_AUTH_OCI_ENDPOINT must be a loopback origin, HTTPS outside Development");
    }

    [Test]
    [Arguments("oci-private-key", null, "TS_AUTH_SIGNING_KEY_VAULT, TS_AUTH_SIGNING_KEY_SECRET and TS_AUTH_OCI_* are partially configured")]
    [Arguments("oci-region", "us-sanjose-1", "TS_AUTH_SIGNING_KEY_VAULT must be a vault OCID in TS_AUTH_OCI_REGION")]
    [Arguments("oci-region", "sa-saopaulo", "TS_AUTH_OCI_REGION must be an OCI region such as sa-saopaulo-1")]
    [Arguments("signing-key-vault", "ocid1.vault.oc2.us-sanjose-1.bbbtest.vaulttest", "TS_AUTH_SIGNING_KEY_VAULT must be a vault OCID in the commercial OCI realm (oc1)")]
    [Arguments("signing-key-vault", "secretvalue", "TS_AUTH_SIGNING_KEY_VAULT must be a vault OCID in the commercial OCI realm (oc1)")]
    [Arguments("signing-key-secret", "Ring_Secret", "TS_AUTH_SIGNING_KEY_SECRET must be a lowercase hyphenated secret name")]
    [Arguments("oci-fingerprint", "not-a-fingerprint", "TS_AUTH_OCI_TENANCY, TS_AUTH_OCI_USER and TS_AUTH_OCI_FINGERPRINT must be OCI identifiers and an API key fingerprint")]
    [Arguments("oci-private-key", "-----BEGIN RSA PRIVATE KEY-----\nsecretvalue\n-----END RSA PRIVATE KEY-----", "TS_AUTH_OCI_PRIVATE_KEY must be an RSA private key in PEM")]
    [Arguments("oci-endpoint", "https://vault.example", "TS_AUTH_OCI_ENDPOINT must be a loopback origin, HTTPS outside Development")]
    [Arguments("oci-endpoint", "https://127.0.0.1:9/ring", "TS_AUTH_OCI_ENDPOINT must be a loopback origin, HTTPS outside Development")]
    [Arguments("oci-endpoint", "https://127.0.0.1:9/?q=1", "TS_AUTH_OCI_ENDPOINT must be an absolute HTTP(S) URL")]
    [Arguments("oci-private-key", "public", "TS_AUTH_OCI_PRIVATE_KEY must be an RSA private key in PEM")]
    public async Task Signing_key_vault_settings_are_validated_without_echoing_them(string key, string? value, string message)
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values =>
        {
            FakeOciVault.For(Guid.NewGuid().ToString()).Bind(values);
            if (value is null)
            {
                values.Remove(key);
            }
            else if (value == "public")
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(values[key]);
                values[key] = rsa.ExportSubjectPublicKeyInfoPem();
            }
            else
            {
                values[key] = value;
            }
        }));

        await Assert.That(exception!.Message).IsEqualTo(message);
        await Assert.That(exception.Message).DoesNotContain("secretvalue");
    }

    [Test]
    [Arguments("https://127.0.0.1:8420", "Production auth storage cannot use a loopback TerraScale API URL")]
    [Arguments("https://evil.example", "Global auth storage requires a TerraScale HTTPS API origin")]
    [Arguments("http://api.terrascale.tech", "TS_AUTH_TERRASCALE_API_URL must be an HTTPS origin without a path")]
    public async Task Production_storage_must_be_the_terrascale_authority(string url, string message)
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values =>
        {
            values.Remove("environment");
            values["terrascale-api-url"] = url;
        }));

        await Assert.That(exception!.Message).IsEqualTo(message);
    }

    [Test]
    [Arguments("terrascale-api-key", "Bearer ", "TS_AUTH_TERRASCALE_API_KEY is required")]
    [Arguments("terrascale-api-key", "bearer  \t ", "TS_AUTH_TERRASCALE_API_KEY is required")]
    [Arguments("database", "auth", "TS_AUTH_DATABASE must be project-uuid/database with a lowercase database name")]
    [Arguments("database", "not-a-uuid/auth/other", "TS_AUTH_DATABASE must be project-uuid/database with a lowercase database name")]
    [Arguments("database", "auth/../other", "TS_AUTH_DATABASE must be project-uuid/database with a lowercase database name")]
    [Arguments("terrascale-api-url", "https://localhost:8443/path", "TS_AUTH_TERRASCALE_API_URL must be an HTTPS origin without a path")]
    [Arguments("terrascale-api-key", "tb_secret\r\nInjected", "TS_AUTH_TERRASCALE_API_KEY must contain at most 4096 characters without whitespace or controls")]
    [Arguments("master-key", "c2hvcnQ=", "TS_AUTH_MASTER_KEY must decode to 32 bytes")]
    [Arguments("master-key", "not base64!", "TS_AUTH_MASTER_KEY must be base64")]
    [Arguments("public-origin", "https://auth.example/?next=x", "TS_AUTH_PUBLIC_ORIGIN must be an absolute HTTP(S) URL")]
    [Arguments("public-origin", "https://auth.example/subpath", "TS_AUTH_PUBLIC_ORIGIN must be an origin without a path")]
    [Arguments("listen", "https://0.0.0.0:8080", "TS_AUTH_LISTEN must be a plain HTTP listen URL")]
    [Arguments("listen", "http://[::1]", "TS_AUTH_LISTEN must use a host and port")]
    [Arguments("listen", "http://127.0.0.1", "TS_AUTH_LISTEN must use a host and port")]
    [Arguments("listen", "http://auth.internal:8080", "TS_AUTH_LISTEN must use a host and port")]
    [Arguments("listen", "http://127.0.0.1:65536", "TS_AUTH_LISTEN must use a host and port")]
    [Arguments("smtp-host", "smtp.example", "TS_AUTH_SMTP_* is partially configured")]
    [Arguments("smtp-check-revocation", "no", "TS_AUTH_SMTP_CHECK_REVOCATION must be true or false")]
    [Arguments("rate-limit", "0", "TS_AUTH_RATE_LIMIT must be positive")]
    [Arguments("trusted-proxies", "proxy.example.invalid", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.0/24", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.1:443", TrustedProxiesError)]
    [Arguments("trusted-proxies", "[2001:db8::1]:443", TrustedProxiesError)]
    [Arguments("trusted-proxies", "[2001:db8::1]", TrustedProxiesError)]
    [Arguments("trusted-proxies", "0.0.0.0", UnspecifiedProxiesError)]
    [Arguments("trusted-proxies", "::", UnspecifiedProxiesError)]
    [Arguments("trusted-proxies", "::ffff:0.0.0.0", UnspecifiedProxiesError)]
    [Arguments("trusted-proxies", "fe80::1%2", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.1", TrustedProxiesError)]
    [Arguments("trusted-proxies", "167772161", TrustedProxiesError)]
    [Arguments("trusted-proxies", "0x0a000001", TrustedProxiesError)]
    [Arguments("trusted-proxies", "010.0.0.1", TrustedProxiesError)]
    [Arguments("trusted-proxies", "::ffff:010.0.0.1", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.256", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.1;", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.1;;10.0.0.2", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.1;10.0.0.1", DuplicateProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.1;::ffff:10.0.0.1", DuplicateProxiesError)]
    [Arguments("trusted-proxies", "2001:db8::1;2001:0db8:0000:0000:0000:0000:0000:0001", DuplicateProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.1;10.0.0.2;10.0.0.3;10.0.0.4;10.0.0.5;10.0.0.6;10.0.0.7;10.0.0.8;10.0.0.9", TooManyProxiesError)]
    [Arguments("terrascale-api-endpoints", "https://10.0.0.1:9465", "TS_AUTH_TERRASCALE_API_ENDPOINTS and TS_AUTH_TERRASCALE_API_CA are required together")]
    [Arguments("terrascale-api-ca", "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----", "TS_AUTH_TERRASCALE_API_CA must hold 1 to 4 PEM certificates and nothing else")]
    [Arguments("terrascale-api-ca", "not a certificate", "TS_AUTH_TERRASCALE_API_CA must hold 1 to 4 PEM certificates and nothing else")]
    [Arguments("terrascale-api-failover", "yes", "TS_AUTH_TERRASCALE_API_FAILOVER must be true or false")]
    public async Task Startup_rejects_malformed_settings(string key, string value, string message)
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values => values[key] = value));

        await Assert.That(exception!.Message).IsEqualTo(message);
    }

    [Test]
    [Arguments("http://10.0.0.1:9465")]
    [Arguments("https://10.0.0.1:9465/path")]
    [Arguments("https://10.0.0.1:9465;https://10.0.0.1:9465")]
    [Arguments("https://user@10.0.0.1:9465")]
    [Arguments("https://1.invalid;https://2.invalid;https://3.invalid;https://4.invalid;https://5.invalid;https://6.invalid;https://7.invalid;https://8.invalid;https://9.invalid")]
    public async Task Private_storage_endpoints_must_be_distinct_https_origins(string endpoints)
    {
        var settings = AuthTestServer.DefaultSettings();
        settings["terrascale-api-endpoints"] = endpoints;
        settings["terrascale-api-ca"] = Root();

        var exception = Assert.Throws<AuthConfigurationException>(() => AuthOptions.Load(settings));
        await Assert.That(exception!.Message).IsEqualTo("TS_AUTH_TERRASCALE_API_ENDPOINTS must hold at most 8 distinct HTTPS origins without a path");
    }

    [Test]
    public async Task Private_storage_trusts_only_pem_certificates()
    {
        var settings = AuthTestServer.DefaultSettings();
        settings["terrascale-api-endpoints"] = "https://10.251.0.21:9465; https://10.251.0.22:9465";
        settings["terrascale-api-ca"] = Root() + "\n" + Root();
        var pinned = Assert.Throws<AuthConfigurationException>(() => AuthOptions.Load(settings));
        await Assert.That(pinned!.Message).IsEqualTo("TS_AUTH_TERRASCALE_API_ENDPOINTS may list several members only with TS_AUTH_TERRASCALE_API_FAILOVER=true");

        settings["terrascale-api-failover"] = "true";
        var options = AuthOptions.Load(settings);
        await Assert.That(options.TerraScaleApiFailover).IsTrue();
        await Assert.That(options.TerraScaleApiEndpoints.Select(endpoint => endpoint.ToString()))
            .IsEquivalentTo(["https://10.251.0.21:9465/", "https://10.251.0.22:9465/"]);
        await Assert.That(options.TerraScaleApiCa.Count).IsEqualTo(2);

        using var key = ECDsa.Create();
        settings["terrascale-api-ca"] = Root() + key.ExportPkcs8PrivateKeyPem();
        var exception = Assert.Throws<AuthConfigurationException>(() => AuthOptions.Load(settings));
        await Assert.That(exception!.Message).IsEqualTo("TS_AUTH_TERRASCALE_API_CA must hold 1 to 4 PEM certificates and nothing else");
    }

    internal static string Root()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=TerraBase member CA test", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.ExportCertificatePem();
    }

    [Test]
    [Arguments(null, true)]
    [Arguments("true", true)]
    [Arguments("false", false)]
    public async Task Smtp_revocation_is_checked_unless_a_deployment_opts_out(string? value, bool expected)
    {
        var settings = AuthTestServer.DefaultSettings();
        if (value is not null)
        {
            settings["smtp-check-revocation"] = value;
        }

        await Assert.That(AuthOptions.Load(settings).SmtpCheckRevocation).IsEqualTo(expected);
    }

    private const string DashboardSecret = "dashboard-config-secret-0123456789abcdef";

    [Test]
    [Arguments(DashboardSecret, null, null, "TS_AUTH_DASHBOARD_CLIENT_SECRET and TS_AUTH_DASHBOARD_REDIRECT_URIS are required together")]
    [Arguments(null, "https://dash.example/cb", null, "TS_AUTH_DASHBOARD_CLIENT_SECRET and TS_AUTH_DASHBOARD_REDIRECT_URIS are required together")]
    [Arguments(null, null, "https://dash.example/out", "TS_AUTH_DASHBOARD_CLIENT_SECRET and TS_AUTH_DASHBOARD_REDIRECT_URIS are required together")]
    [Arguments("too-short-dashboard-secret", "https://dash.example/cb", null, "TS_AUTH_DASHBOARD_CLIENT_SECRET must contain 32 to 512 characters without whitespace or controls")]
    [Arguments("dashboard secret with spaces 0123456789", "https://dash.example/cb", null, "TS_AUTH_DASHBOARD_CLIENT_SECRET must contain 32 to 512 characters without whitespace or controls")]
    [Arguments(DashboardSecret, "http://dash.example/cb", null, "TS_AUTH_DASHBOARD_REDIRECT_URIS must hold absolute HTTPS URIs (HTTP only for loopback) without credentials or fragments")]
    [Arguments(DashboardSecret, "https://dash.example/cb#fragment", null, "TS_AUTH_DASHBOARD_REDIRECT_URIS must hold absolute HTTPS URIs (HTTP only for loopback) without credentials or fragments")]
    [Arguments(DashboardSecret, "https://user:pass@dash.example/cb", null, "TS_AUTH_DASHBOARD_REDIRECT_URIS must hold absolute HTTPS URIs (HTTP only for loopback) without credentials or fragments")]
    [Arguments(DashboardSecret, "/relative/cb", null, "TS_AUTH_DASHBOARD_REDIRECT_URIS must hold absolute HTTPS URIs (HTTP only for loopback) without credentials or fragments")]
    [Arguments(DashboardSecret, "https://a.example/1;https://a.example/2;https://a.example/3;https://a.example/4;https://a.example/5;https://a.example/6;https://a.example/7;https://a.example/8;https://a.example/9;https://a.example/10;https://a.example/11", null, "TS_AUTH_DASHBOARD_REDIRECT_URIS may contain at most 10 URIs")]
    [Arguments(DashboardSecret, "https://dash.example/cb", "http://dash.example/out", "TS_AUTH_DASHBOARD_POST_LOGOUT_REDIRECT_URIS must hold absolute HTTPS URIs (HTTP only for loopback) without credentials or fragments")]
    public async Task The_dashboard_client_settings_are_validated_together(string? secret, string? redirects, string? postLogoutRedirects, string message)
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values =>
        {
            foreach (var (key, value) in new[] { ("dashboard-client-secret", secret), ("dashboard-redirect-uris", redirects), ("dashboard-post-logout-redirect-uris", postLogoutRedirects) })
            {
                if (value is not null)
                {
                    values[key] = value;
                }
            }
        }));

        await Assert.That(exception!.Message).IsEqualTo(message);
    }

    private const string ArgumentSecret = "sktestvaluethatmustnotbeprinted";

    [Test]
    [Arguments("--terrascale-api-ke=" + ArgumentSecret, "Unknown option (expected --key=value with a TS_AUTH_* setting name)")]
    [Arguments("--correct-horse-battery-staple-" + ArgumentSecret + "=x", "Unknown option (expected --key=value with a TS_AUTH_* setting name)")]
    [Arguments("--operator-key:" + ArgumentSecret, "Malformed option (expected --key=value)")]
    [Arguments("--" + ArgumentSecret, "Malformed option (expected --key=value)")]
    [Arguments("--operator-key:" + ArgumentSecret + "=x", "Unknown option (expected --key=value with a TS_AUTH_* setting name)")]
    public async Task The_service_binary_rejects_bad_options_without_echoing_their_values(string argument, string message)
    {
        var (exitCode, output) = await RunBinaryAsync(argument);

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(output).Contains(message);
        await Assert.That(output).DoesNotContain(ArgumentSecret);
    }

    [Test]
    public async Task The_service_binary_reports_configuration_errors_without_values_or_a_stack_trace()
    {
        var (exitCode, output) = await RunBinaryAsync("--master-key=not-base64!", AuthTestServer.DefaultSettings());

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(output).Contains("TS_AUTH_MASTER_KEY must be base64");
        await Assert.That(output).DoesNotContain("not-base64!");
        await Assert.That(output).DoesNotContain("   at ");
    }

    [Test]
    public async Task Command_line_options_override_the_environment()
    {
        var settings = AuthTestServer.DefaultSettings();
        settings["public-origin"] = "https://env.example.invalid";
        await using var service = await RunningBinary.StartAsync(settings, "--public-origin=https://cli.example.invalid");

        using var response = await service.Client.GetAsync("/openapi/v1.json");
        var document = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(await response.Content.ReadAsStringAsync());

        await Assert.That(document.GetProperty("servers")[0].GetProperty("url").GetString()).IsEqualTo("https://cli.example.invalid");
    }

    [Test]
    [Arguments(null, false)]
    [Arguments("10.0.0.1;2001:db8::1;::ffff:10.0.0.2", false)]
    [Arguments("10.0.0.1;10.0.0.2;10.0.0.3;10.0.0.4;10.0.0.5;10.0.0.6;10.0.0.7;10.0.0.8", false)]
    [Arguments("10.0.0.1;2001:db8::1", true)]
    public async Task The_service_binary_accepts_numeric_proxy_settings_from_the_environment_or_command_line(string? proxies, bool commandLine)
    {
        var settings = AuthTestServer.DefaultSettings();
        if (proxies is not null)
        {
            settings["trusted-proxies"] = commandLine ? "invalid-proxy.example.invalid" : proxies;
        }

        await using var service = await RunningBinary.StartAsync(settings, commandLine ? ["--trusted-proxies=" + proxies] : []);
        using var health = await service.Client.GetAsync("/healthz");
        await Assert.That(health.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
    }

    [Test]
    [ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
    public async Task Global_forwarding_flags_cannot_enable_untrusted_hops_bypass_empty_tails_or_remove_the_proxy_aggregate(TerraBaseFixture fixture)
    {
        const string path = "/terrascale/oauth/token";
        const int clientCapacity = 1;
        const int emptyTailAttempts = 2;
        var proxyCapacity = clientCapacity * TokenRateLimitMiddleware.ProxyFactor;
        var forwardedClientCount = proxyCapacity - emptyTailAttempts;
        foreach (var (proxies, listen) in new (string? Proxies, string Listen)[]
        {
            (null, "http://127.0.0.1:0"),
            ("10.0.0.1", "http://127.0.0.1:0"),
            ("127.0.0.1", "http://127.0.0.1:0"),
            // IPv4 connections to a dual-stack listener arrive as IPv4-mapped IPv6 peers.
            ("127.0.0.1", "http://[::]:0"),
        })
        {
            var scenario = $"trusted-proxies={proxies ?? "<unset>"}, listen={listen}";
            var trusted = proxies == "127.0.0.1";
            var target = await SharedStateTarget.CreateAsync(fixture);
            var settings = AuthTestServer.DefaultSettings();
            target.Bind(settings);
            settings["terrascale-api-endpoints"] = fixture.Server.ClientAddress.GetLeftPart(UriPartial.Authority);
            settings["terrascale-api-ca"] = fixture.Server.Certificate.ExportCertificatePem();
            settings["rate-limit"] = clientCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings["listen"] = listen;
            if (proxies is not null)
            {
                settings["trusted-proxies"] = proxies;
            }

            // A persisted ahead-of-clock window cannot roll over during the live binary's bounded request sequence.
            var clock = new ManualClock(DateTimeOffset.UtcNow.AddDays(1));
            var limiter = new AttemptLimiter(new TerraBaseDocumentStore(new AuthStore(target.Database, clock)), clock, AuthOptions.Load(settings));
            var clients = Enumerable.Range(1, 254).Select(number => $"203.0.113.{number}")
                .Where(address => limiter.Fold(address) != limiter.Fold("127.0.0.1"))
                .DistinctBy(limiter.Fold).Take(forwardedClientCount + 1).ToArray();
            var subjects = new List<(string Purpose, string Subject)> { (trusted ? "token-proxy-ip" : "token-peer-ip", "127.0.0.1") };
            if (trusted)
            {
                subjects.Add(("token-client-ip", "127.0.0.1"));
                subjects.AddRange(clients.Take(forwardedClientCount).Select(subject => ("token-client-ip", subject)));
            }

            foreach (var (purpose, subject) in subjects)
            {
                await Assert.That(await limiter.TryAcquireAsync(OidcIssuer.InstanceId, purpose, limiter.Fold(subject), clientCapacity, CancellationToken.None)).IsTrue().Because(scenario);
            }

            foreach (var bucket in (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items)
            {
                await target.Database.PutDocumentAsync(AttemptLimiter.Collection, bucket.Id,
                    JsonSerializer.SerializeToElement(new { window = bucket.Document.GetProperty("window").GetInt64(), used = 0 }), TerraBasePrecondition.Matches(bucket.Version));
            }

            await using var service = await RunningBinary.StartAsync(settings, new Dictionary<string, string>
            {
                ["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true",
                ["ForwardedHeaders_Enabled"] = "true",
            });

            var (first, _) = await RawHttp.DuplicateForwardedAsync(service.Client.BaseAddress!, path, clients[0], trusted ? string.Empty : clients[0], 0);
            await Assert.That(first).IsEqualTo(HttpStatusCode.Unauthorized).Because(scenario);
            var (second, delay) = await RawHttp.DuplicateForwardedAsync(service.Client.BaseAddress!, path, clients[1], trusted ? " \t " : clients[1], 1);
            await Assert.That(second).IsEqualTo(HttpStatusCode.TooManyRequests).Because(scenario);
            await Assert.That(delay).IsNotNull().Because(scenario);

            if (trusted)
            {
                // Empty final wire lines spent the socket's client allowance, neither preceding fake client's allowance.
                for (var attempt = 0; attempt < forwardedClientCount; attempt++)
                {
                    var (status, _) = await RawHttp.DuplicateForwardedAsync(service.Client.BaseAddress!, path, clients[attempt], clients[attempt], attempt + emptyTailAttempts);
                    await Assert.That(status).IsEqualTo(HttpStatusCode.Unauthorized).Because(scenario);
                }

                // The client-budget refusal also spent the original proxy's aggregate budget.
                var (limited, retryAfter) = await RawHttp.DuplicateForwardedAsync(service.Client.BaseAddress!, path, clients[forwardedClientCount], clients[forwardedClientCount], proxyCapacity);
                await Assert.That(limited).IsEqualTo(HttpStatusCode.TooManyRequests).Because(scenario);
                await Assert.That(retryAfter).IsNotNull().Because(scenario);
            }

            var buckets = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items;
            var expectedClientBuckets = trusted ? 1 + forwardedClientCount : 0;
            var expectedBucketCount = 1 + expectedClientBuckets;
            var expectedPeerUsed = trusted ? proxyCapacity : clientCapacity;
            var expectedUsed = expectedPeerUsed + expectedClientBuckets * clientCapacity;
            await Assert.That(buckets.Count).IsEqualTo(expectedBucketCount).Because(scenario);
            await Assert.That(buckets.All(bucket => bucket.Document.GetProperty("window").GetInt64() == clock.GetUtcNow().ToUnixTimeSeconds() / 60)).IsTrue().Because(scenario);
            await Assert.That(buckets.Sum(bucket => bucket.Document.GetProperty("used").GetInt32())).IsEqualTo(expectedUsed).Because(scenario);
            using var discoveryRequest = new HttpRequestMessage(HttpMethod.Get, "/terrascale/.well-known/openid-configuration");
            discoveryRequest.Headers.Host = "spoofed.example.invalid";
            discoveryRequest.Headers.Add("X-Forwarded-For", clients[forwardedClientCount]);
            discoveryRequest.Headers.Add("X-Forwarded-Host", "spoofed.example.invalid");
            discoveryRequest.Headers.Add("X-Forwarded-Proto", "https");
            using var discovery = await service.Client.SendAsync(discoveryRequest);
            await Assert.That(discovery.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(scenario);
            var document = await OidcHarness.JsonAsync(discovery);
            await Assert.That(document.GetProperty("issuer").GetString()).IsEqualTo(OidcHarness.Issuer).Because(scenario);
            await Assert.That(document.GetProperty("token_endpoint").GetString()).IsEqualTo(OidcHarness.Issuer + "/oauth/token").Because(scenario);
        }
    }

    [Test]
    [Arguments("https://api.terrascale.tech")]
    [Arguments("https://gru.api.terrascale.tech")]
    public async Task Production_starts_with_either_permitted_storage_host(string apiUrl)
    {
        var settings = AuthTestServer.DefaultSettings();
        settings.Remove("environment");
        settings["terrascale-api-url"] = apiUrl;
        settings["terrascale-api-key"] = "Bearer  sk_test_production_start";
        FakeOciVault.BindRefusing(settings);
        await using var service = await RunningBinary.StartAsync(settings);

        using var health = await service.Client.GetAsync("/healthz");

        await Assert.That(health.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
        // Exercise startup without sending any request to a production storage endpoint.
    }

    /// <summary>The real service binary on a free loopback port, with a controlled environment.</summary>
    private sealed class RunningBinary : IAsyncDisposable
    {
        private readonly Process process;

        private RunningBinary(Process process)
        {
            this.process = process;
            this.Client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        public HttpClient Client { get; }

        // Log records are JSON lines, after an optional journald <N> prefix; the "Now listening on"
        // record carries its address as a value.
        private static string? ListeningAddress(string line)
        {
            var start = line.IndexOf('{', StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            try
            {
                using var record = JsonDocument.Parse(line[start..]);
                return record.RootElement.TryGetProperty("msg", out var msg) && msg.GetString()?.Contains("Now listening on", StringComparison.Ordinal) == true
                    && Regex.Match(line, "https?://[^\"]+") is { Success: true } address
                    ? address.Value
                    : null;
            }
            catch (JsonException)
            {
                // A partial line; the next complete record carries the address.
                return null;
            }
        }

        public static Task<RunningBinary> StartAsync(Dictionary<string, string> settings, params string[] arguments) =>
            StartAsync(settings, [], arguments);

        public static async Task<RunningBinary> StartAsync(Dictionary<string, string> settings, Dictionary<string, string> environment, params string[] arguments)
        {
            // Port 0: the child binds a free port itself and reports it on its startup line,
            // so no other process can answer in its place.
            settings.TryAdd("listen", "http://127.0.0.1:0");
            var start = new ProcessStartInfo("dotnet", [Path.Combine(AppContext.BaseDirectory, "ts-auth.dll"), .. arguments])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("TS_AUTH_", StringComparison.Ordinal) || key.StartsWith("OTEL_", StringComparison.Ordinal) || key.StartsWith("Logging__", StringComparison.Ordinal)).ToList())
            {
                start.Environment.Remove(name);
            }

            foreach (var (key, value) in settings)
            {
                start.Environment[$"TS_AUTH_{key.Replace('-', '_').ToUpperInvariant()}"] = value;
            }

            foreach (var (key, value) in environment)
            {
                start.Environment[key] = value;
            }

            var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = Process.Start(start)!;
            process.OutputDataReceived += (_, line) =>
            {
                if (line.Data is { } data && ListeningAddress(data) is { } address)
                {
                    listening.TrySetResult(new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)));
                }
            };
            process.ErrorDataReceived += (_, _) => { };
            process.Exited += (_, _) => listening.TrySetException(new InvalidOperationException("ts-auth exited before listening."));
            process.EnableRaisingEvents = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var service = new RunningBinary(process);
            var started = false;
            try
            {
                service.Client.BaseAddress = await listening.Task.WaitAsync(TimeSpan.FromSeconds(30));
                using var probe = await service.Client.GetAsync("/healthz");
                started = true;
                return service;
            }
            finally
            {
                // Every unsuccessful path (timeouts and cancellation included) stops the child.
                if (!started)
                {
                    await service.DisposeAsync();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            this.Client.Dispose();
            if (!this.process.HasExited)
            {
                this.process.Kill(entireProcessTree: true);
                await this.process.WaitForExitAsync();
            }

            this.process.Dispose();
        }
    }

    private static async Task<(int ExitCode, string Output)> RunBinaryAsync(string argument, Dictionary<string, string>? settings = null)
    {
        var binary = Path.Combine(AppContext.BaseDirectory, "ts-auth.dll");
        var start = new ProcessStartInfo("dotnet", [binary, argument])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // A developer's own TS_AUTH_* settings must not change the outcome.
        foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("TS_AUTH_", StringComparison.Ordinal)).ToList())
        {
            start.Environment.Remove(name);
        }

        foreach (var (key, value) in settings ?? [])
        {
            start.Environment[$"TS_AUTH_{key.Replace('-', '_').ToUpperInvariant()}"] = value;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            // A regression that lets the server start must fail the test, not hang it.
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        return (process.ExitCode, await stdout + await stderr);
    }
}
