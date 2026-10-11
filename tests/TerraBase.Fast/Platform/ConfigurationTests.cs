using TerraBase.Configuration;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class ConfigurationTests
{
    [Test]
    public async Task Nats_needs_tls_or_nats_servers_and_user_credentials_and_never_echoes_them()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-nats-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            var other = Path.Combine(directory, "other");
            await File.WriteAllTextAsync(credentials, "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n");
            await File.WriteAllTextAsync(other, "secret-value");
            var settings = TerraBaseOptions.FromValues(new Dictionary<string, string> { ["nats-url"] = "tls://10.0.0.1:4222, tls://10.0.0.2:4222", ["nats-credentials-file"] = credentials });

            var noCredentials = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["nats-url"] = "tls://10.0.0.1:4222" }))
                .Throws<TerraBaseConfigurationException>();
            var notCredentials = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["nats-url"] = "tls://10.0.0.1:4222", ["nats-credentials-file"] = other }))
                .Throws<TerraBaseConfigurationException>();
            var badUrl = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["nats-url"] = "https://user:secret-value@host", ["nats-credentials-file"] = credentials }))
                .Throws<TerraBaseConfigurationException>();
            var partialTls = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["nats-url"] = "tls://10.0.0.1:4222", ["nats-credentials-file"] = credentials, ["nats-ca"] = other }))
                .Throws<TerraBaseConfigurationException>();
            var urlMissing = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["nats-credentials-file"] = credentials }))
                .Throws<TerraBaseConfigurationException>();
            var partialBackup = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["backup-bucket"] = "gru-1-backup" }))
                .Throws<TerraBaseConfigurationException>();
            using var oci = new FakeOci();
            var cluster = new Dictionary<string, string>
            {
                ["peer-port"] = "0",
                ["peer-certificate"] = other,
                ["peer-private-key"] = other,
                ["peer-client-ca"] = other,
                ["peer-addresses"] = "https://peer.test:9443",
            };
            oci.Configure(cluster);
            var clusterWithoutNats = await Assert.That(() => TerraBaseOptions.FromValues(cluster)).Throws<TerraBaseConfigurationException>();

            await Assert.That(settings.Nats!.Url).IsEqualTo("tls://10.0.0.1:4222,tls://10.0.0.2:4222");
            await Assert.That(settings.Nats.ToString()).DoesNotContain("creds");
            await Assert.That(noCredentials!.Message).Contains("nats-credentials-file");
            await Assert.That(notCredentials!.Message).Contains("nats-credentials-file");
            await Assert.That(badUrl!.Message).Contains("nats-url");
            await Assert.That(badUrl.Message).DoesNotContain("secret-value");
            await Assert.That(partialTls!.Message).Contains("nats-certificate");
            await Assert.That(urlMissing!.Message).Contains("nats-url");
            await Assert.That(partialBackup!.Message).Contains("backup-secret-key-file");
            await Assert.That(clusterWithoutNats!.Message).Contains("nats-url");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Backup_retention_defaults_to_one_seven_and_thirty_days_and_takes_plan_overrides()
    {
        using var oci = new FakeOci();
        var values = new Dictionary<string, string>();
        oci.Configure(values);
        var defaults = TerraBaseOptions.FromValues(values).Backup!.RetentionDays;
        values["backup-retention-days"] = "pro=14, enterprise=90";
        var overridden = TerraBaseOptions.FromValues(values).Backup!.RetentionDays;
        values["backup-retention-days"] = "free=1";
        var unknownPlan = await Assert.That(() => TerraBaseOptions.FromValues(values)).Throws<TerraBaseConfigurationException>();
        values["backup-retention-days"] = "hobby=0";
        var zero = await Assert.That(() => TerraBaseOptions.FromValues(values)).Throws<TerraBaseConfigurationException>();
        var withoutBackup = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["backup-retention-days"] = "hobby=2" }))
            .Throws<TerraBaseConfigurationException>();

        await Assert.That(defaults[PlanTier.Hobby]).IsEqualTo(1);
        await Assert.That(defaults[PlanTier.Pro]).IsEqualTo(7);
        await Assert.That(defaults[PlanTier.Enterprise]).IsEqualTo(30);
        await Assert.That(overridden[PlanTier.Hobby]).IsEqualTo(1);
        await Assert.That(overridden[PlanTier.Pro]).IsEqualTo(14);
        await Assert.That(overridden[PlanTier.Enterprise]).IsEqualTo(90);
        await Assert.That(unknownPlan!.Message).Contains("backup-retention-days");
        await Assert.That(zero!.Message).Contains("backup-retention-days");
        await Assert.That(withoutBackup!.Message).Contains("backup-endpoint");
    }

    [Test]
    public async Task Unknown_settings_are_rejected_without_echoing_values()
    {
        var error = await Assert.That(() => TerraBaseOptions.FromProcess(["--admin-token=secret-value"]))
            .Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains("admin-token");
        await Assert.That(error.Message).DoesNotContain("secret-value");
    }

    [Test]
    [Arguments("-1")]
    [Arguments("65536")]
    [Arguments("port")]
    public async Task Invalid_admin_ports_are_rejected(string port)
    {
        await Assert.That(() => TerraBaseOptions.FromProcess([$"--admin-port={port}"]))
            .Throws<TerraBaseConfigurationException>();
    }

    [Test]
    [Arguments("-1")]
    [Arguments("3601")]
    [Arguments("10s")]
    public async Task Invalid_shutdown_grace_periods_are_rejected(string seconds)
    {
        var error = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["shutdown-grace-seconds"] = seconds }))
            .Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains("shutdown-grace-seconds");
    }

    [Test]
    public async Task The_shutdown_grace_period_defaults_to_ten_seconds_and_can_be_zero()
    {
        await Assert.That(TerraBaseOptions.FromValues(new Dictionary<string, string>()).ShutdownGrace).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(TerraBaseOptions.FromValues(new Dictionary<string, string> { ["shutdown-grace-seconds"] = "0" }).ShutdownGrace)
            .IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    [Arguments("memory-budget", "1024")]
    [Arguments("memory-budget", "lots")]
    [Arguments("storage-threads", "0")]
    [Arguments("storage-threads", "4096")]
    [Arguments("storage-queue", "0")]
    [Arguments("storage-queue", "-1")]
    public async Task Invalid_memory_and_storage_limits_are_rejected(string setting, string value)
    {
        var error = await Assert.That(() => TerraBaseOptions.FromProcess([$"--{setting}={value}"]))
            .Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains(setting);
    }

    [Test]
    public async Task Invalid_data_directories_are_rejected_without_echoing_them()
    {
        var error = await Assert.That(() => TerraBaseOptions.FromProcess(["--data-directory=secret\0path"]))
            .Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains("data-directory");
        await Assert.That(error.Message).DoesNotContain("secret");
    }

    [Test]
    public async Task An_empty_argument_clears_a_setting()
    {
        var options = TerraBaseOptions.FromProcess(["--peer-port=", "--admin-port=0"]);

        await Assert.That(options.PeerPort).IsNull();
        await Assert.That(options.AdminPort).IsEqualTo(0);
    }

    [Test]
    public async Task The_auth_issuer_needs_an_audience_and_the_client_listener()
    {
        using var tls = new ClientTls();
        var client = new Dictionary<string, string>();
        tls.Apply(client);
        var configured = new Dictionary<string, string>(client);
        TestIssuer.Configure(configured);

        var issuerOnly = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string>(client) { ["auth-issuer"] = TestIssuer.Issuer }))
            .Throws<TerraBaseConfigurationException>();
        var audienceOnly = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string>(client) { ["auth-audience"] = TestIssuer.Audience }))
            .Throws<TerraBaseConfigurationException>();
        var withoutClient = await Assert.That(() => TerraBaseOptions.FromValues(
                new Dictionary<string, string> { ["auth-issuer"] = TestIssuer.Issuer, ["auth-audience"] = TestIssuer.Audience }))
            .Throws<TerraBaseConfigurationException>();
        var removed = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string>(configured) { ["dashboard-credential-file"] = "credential" }))
            .Throws<TerraBaseConfigurationException>();
        var options = TerraBaseOptions.FromValues(configured);
        var several = TerraBaseOptions.FromValues(new Dictionary<string, string>(configured) { ["auth-audience"] = "https://a.test, b " });
        var unset = TerraBaseOptions.FromValues(client);

        await Assert.That(issuerOnly!.Message).Contains("auth-issuer and auth-audience must be set together");
        await Assert.That(audienceOnly!.Message).Contains("auth-issuer and auth-audience must be set together");
        await Assert.That(withoutClient!.Message).Contains("auth-issuer requires client-port");
        // The dashboard credential setting is gone (unknown); management-credential-file replaces it, admin listener only.
        await Assert.That(removed!.Message).Contains("dashboard-credential-file");
        await Assert.That(options.AuthIssuer).IsEqualTo(TestIssuer.Issuer);
        await Assert.That(options.AuthAudiences).IsEquivalentTo([TestIssuer.Audience]);
        await Assert.That(several.AuthAudiences).IsEquivalentTo(["https://a.test", "b"]);
        await Assert.That(unset.AuthIssuer).IsNull();
        await Assert.That(unset.AuthAudiences).IsEmpty();
    }

    [Test]
    public async Task The_management_credential_file_needs_a_long_secret_and_no_client_listener()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-credential-").FullName;
        try
        {
            var secretFile = Path.Combine(directory, "secret");
            await File.WriteAllTextAsync(secretFile, "  " + new string('s', 32) + "\n");
            var shortFile = Path.Combine(directory, "short");
            await File.WriteAllTextAsync(shortFile, new string('s', 31) + "\n");

            var options = TerraBaseOptions.FromValues(new Dictionary<string, string> { ["management-credential-file"] = secretFile });
            var unset = TerraBaseOptions.FromValues(new Dictionary<string, string>());
            var tooShort = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["management-credential-file"] = shortFile }))
                .Throws<TerraBaseConfigurationException>();
            var missing = await Assert.That(() => TerraBaseOptions.FromValues(
                    new Dictionary<string, string> { ["management-credential-file"] = Path.Combine(directory, "missing") }))
                .Throws<TerraBaseConfigurationException>();

            // Only the hash of the trimmed secret is kept.
            await Assert.That(options.ManagementCredentialHash.ToArray())
                .IsEquivalentTo(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(new string('s', 32))));
            await Assert.That(unset.ManagementCredentialHash.IsEmpty).IsTrue();
            await Assert.That(tooShort!.Message).Contains("management-credential-file must hold a secret of at least 32 characters");
            await Assert.That(missing!.Message).Contains("management-credential-file must name a readable file");
            await Assert.That(missing.Message).DoesNotContain(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments("http://auth.test/terrascale")]
    [Arguments("auth.test/terrascale")]
    [Arguments("https://user:secret@auth.test/terrascale")]
    [Arguments("https://auth.test/terrascale?tenant=secret")]
    [Arguments("https://auth.test/terrascale#secret")]
    [Arguments(" https://auth.test/terrascale")]
    [Arguments("https://auth.test/terrascale\n")]
    [Arguments("https://auth.test/terra scale")]
    public async Task Auth_issuers_must_be_plain_https_urls_and_are_not_echoed(string issuer)
    {
        using var tls = new ClientTls();
        var settings = new Dictionary<string, string> { ["auth-issuer"] = issuer, ["auth-audience"] = TestIssuer.Audience };
        tls.Apply(settings);

        var error = await Assert.That(() => TerraBaseOptions.FromValues(settings)).Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains("auth-issuer must be an absolute https URL without whitespace, user information, query or fragment");
        await Assert.That(error.Message).DoesNotContain("auth.test");
    }

    [Test]
    [Arguments("a,,b")]
    [Arguments("a, ,b")]
    [Arguments("a,")]
    [Arguments("a\u0001b")]
    [Arguments("\ta,b")]
    [Arguments("a\r,b")]
    [Arguments("a,b\n")]
    public async Task Auth_audiences_must_be_non_empty_without_control_characters(string audience)
    {
        using var tls = new ClientTls();
        var settings = new Dictionary<string, string> { ["auth-issuer"] = TestIssuer.Issuer, ["auth-audience"] = audience };
        tls.Apply(settings);

        var error = await Assert.That(() => TerraBaseOptions.FromValues(settings)).Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains("auth-audience must be a comma-separated list of non-empty audiences without control characters");
    }

}
