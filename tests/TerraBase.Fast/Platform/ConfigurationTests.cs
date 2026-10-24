using TerraBase.Configuration;
using TerraBase.Engine.Cluster;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class ConfigurationTests
{
    [Test]
    public async Task A_node_takes_its_identity_and_peers_from_ts_orch_and_a_process_elsewhere_is_local()
    {
        const string ChildFlag = "TERRABASE_TEST_CLUSTER_IDENTITY_CHILD";
        if (Environment.GetEnvironmentVariable(ChildFlag) != "1")
        {
            // The TS_* variables are process-wide: only a process that runs this test alone may set them.
            await IsolatedTestChild.RunAsync(typeof(ConfigurationTests), nameof(A_node_takes_its_identity_and_peers_from_ts_orch_and_a_process_elsewhere_is_local), ChildFlag);
            return;
        }

        var local = TerraBaseOptions.FromValues(new Dictionary<string, string>());
        await Assert.That((local.Region, local.MachineId, local.PeersFile)).IsEqualTo(("local", "local", (string?)null));
        // A production node trusts the platform's tokens; Development trusts none unless set.
        var production = TerraBaseOptions.FromValues(new Dictionary<string, string>(), production: true);
        await Assert.That((production.AuthIssuer, production.AuthAudiences.Single())).IsEqualTo((TerraBaseOptions.PlatformAuthIssuer, TerraBaseOptions.PlatformAuthAudience));
        await Assert.That(local.AuthIssuer).IsNull();

        var directory = Directory.CreateTempSubdirectory("terrabase-peers-").FullName;
        try
        {
            var peers = Path.Combine(directory, "peers");
            // ts-orch's lines; a malformed one is no member.
            await File.WriteAllTextAsync(peers, "ts-sjc-1 sjc 10.200.109.193\ngru-1-ctl-1 gru 10.200.165.148\nNot A Node fra 10.200.40.238\nts-fra-1 fra not-an-ip\n");
            Environment.SetEnvironmentVariable("TS_REGION", "sjc");
            Environment.SetEnvironmentVariable("TS_NODE", "ts-sjc-1");
            Environment.SetEnvironmentVariable("TS_PEERS_FILE", peers);
            var node = TerraBaseOptions.FromValues(new Dictionary<string, string> { ["internal-port"] = "18081" });
            await Assert.That((node.Region, node.MachineId, node.PeersFile)).IsEqualTo(("sjc", "ts-sjc-1", (string?)peers));
            await Assert.That(PeersFile.SelfAddress(node).ToString()).IsEqualTo("10.200.109.193");
            var members = await new PeersFile(node).ListAsync(CancellationToken.None);
            await Assert.That(members).IsEquivalentTo([
                new ClusterMember("ts-sjc-1", "http://10.200.109.193:18081/", "sjc", string.Empty),
                new ClusterMember("gru-1-ctl-1", "http://10.200.165.148:18081/", "gru", string.Empty),
            ]);

            // A node missing from the file never binds a public address in its place.
            Environment.SetEnvironmentVariable("TS_NODE", "ts-sjc-2");
            var missing = await Assert.That(() => PeersFile.SelfAddress(TerraBaseOptions.FromValues(new Dictionary<string, string>()))).Throws<TerraBaseConfigurationException>();
            await Assert.That(missing!.Message).Contains("TS_PEERS_FILE");

            // ts-orch sets all three together; a partial or malformed identity never starts, and the error names no value.
            Environment.SetEnvironmentVariable("TS_NODE", "Not A Node");
            var malformed = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string>())).Throws<TerraBaseConfigurationException>();
            await Assert.That(malformed!.Message).Contains("TS_NODE");
            await Assert.That(malformed.Message).DoesNotContain("Not A Node");
            Environment.SetEnvironmentVariable("TS_NODE", null);
            await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string>())).Throws<TerraBaseConfigurationException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Production_trusts_only_the_one_platform_issuer_and_audience_ts_auth_issues_in_every_region()
    {
        static Dictionary<string, string> Auth(string issuer, string audience)
        {
            return new() { ["auth-issuer"] = issuer, ["auth-audience"] = audience };
        }

        // gru's spec sets the platform pair explicitly; the same token is accepted in gru, sjc and fra.
        var production = TerraBaseOptions.FromValues(Auth(TerraBaseOptions.PlatformAuthIssuer, TerraBaseOptions.PlatformAuthAudience), production: true);
        await Assert.That((production.AuthIssuer, production.AuthAudiences.Single())).IsEqualTo(("https://auth.terrascale.tech/terrascale", "https://api.terrascale.tech"));
        foreach (var (issuer, audience) in new[]
        {
            ("https://attacker.example/terrascale", TerraBaseOptions.PlatformAuthAudience),
            ("https://gru.auth.terrascale.tech/terrascale", TerraBaseOptions.PlatformAuthAudience),
            ("https://auth.terrascale.tech", TerraBaseOptions.PlatformAuthAudience),
            (TerraBaseOptions.PlatformAuthIssuer, "https://gru.api.terrascale.tech"),
        })
        {
            var refused = await Assert.That(() => TerraBaseOptions.FromValues(Auth(issuer, audience), production: true)).Throws<TerraBaseConfigurationException>();
            await Assert.That(refused!.Message).IsEqualTo("outside Development, auth-issuer must be https://auth.terrascale.tech/terrascale and auth-audience must include https://api.terrascale.tech: ts-auth issues every region's platform tokens with that pair");
        }

        // Development (tests, local runs) trusts the ts-auth it names.
        var development = TerraBaseOptions.FromValues(Auth("https://127.0.0.1:5001/terrascale", "terrabase-test"));
        await Assert.That(development.AuthIssuer).IsEqualTo("https://127.0.0.1:5001/terrascale");
    }

    [Test]
    public async Task Partial_backup_settings_name_a_missing_one()
    {
        var partialBackup = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["backup-bucket"] = "gru-1-backup" }))
            .Throws<TerraBaseConfigurationException>();
        await Assert.That(partialBackup!.Message).Contains("master-key");
    }

    [Test]
    public async Task Backup_retention_matches_plan_entitlements_and_refuses_process_overrides()
    {
        var s3 = new FakeS3();
        var values = new Dictionary<string, string>();
        s3.Configure(values);
        var defaults = TerraBaseOptions.FromValues(values).Backup!.RetentionDays;
        await Assert.That(defaults[PlanTier.Hobby]).IsEqualTo(1);
        await Assert.That(defaults[PlanTier.Starter]).IsEqualTo(3);
        await Assert.That(defaults[PlanTier.Pro]).IsEqualTo(7);
        await Assert.That(defaults[PlanTier.Team]).IsEqualTo(14);
        await Assert.That(defaults[PlanTier.Enterprise]).IsEqualTo(30);
        values["backup-retention-days"] = "hobby=7,pro=7,enterprise=7";
        var removed = await Assert.That(() => TerraBaseOptions.FromValues(values)).Throws<TerraBaseConfigurationException>();
        await Assert.That(removed!.Message).Contains("unknown setting 'backup-retention-days'");
    }

    [Test]
    [Arguments("c2hvcnQ=")]
    [Arguments("not base64 at all")]
    [Arguments("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_master_key_that_is_not_32_base64_bytes_is_rejected_without_echoing_it(string masterKey)
    {
        var values = new Dictionary<string, string>();
        new FakeS3().Configure(values);
        values["master-key"] = masterKey;

        var error = await Assert.That(() => TerraBaseOptions.FromValues(values)).Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).IsEqualTo("master-key must be 32 bytes, base64");
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
        var options = TerraBaseOptions.FromProcess(["--internal-port=", "--port=0"]);

        await Assert.That(options.InternalPort).IsEqualTo(TerraBaseOptions.DefaultInternalPort);
        await Assert.That(options.Port).IsEqualTo(0);
    }

    [Test]
    public async Task The_auth_issuer_needs_an_audience()
    {
        var client = new Dictionary<string, string>();
        var configured = new Dictionary<string, string>(client);
        TestIssuer.Configure(configured);

        var issuerOnly = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string>(client) { ["auth-issuer"] = TestIssuer.Issuer }))
            .Throws<TerraBaseConfigurationException>();
        var audienceOnly = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string>(client) { ["auth-audience"] = TestIssuer.Audience }))
            .Throws<TerraBaseConfigurationException>();
        var removed = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string>(configured) { ["dashboard-credential-file"] = "credential" }))
            .Throws<TerraBaseConfigurationException>();
        var options = TerraBaseOptions.FromValues(configured);
        var several = TerraBaseOptions.FromValues(new Dictionary<string, string>(configured) { ["auth-audience"] = "https://a.test, b " });
        var unset = TerraBaseOptions.FromValues(client);

        await Assert.That(issuerOnly!.Message).Contains("auth-issuer and auth-audience must be set together");
        await Assert.That(audienceOnly!.Message).Contains("auth-issuer and auth-audience must be set together");
        // The dashboard credential setting is gone (unknown): the internal listener trusts the platform operator instead.
        await Assert.That(removed!.Message).Contains("dashboard-credential-file");
        await Assert.That(options.AuthIssuer).IsEqualTo(TestIssuer.Issuer);
        await Assert.That(options.AuthAudiences).IsEquivalentTo([TestIssuer.Audience]);
        await Assert.That(several.AuthAudiences).IsEquivalentTo(["https://a.test", "b"]);
        await Assert.That(unset.AuthIssuer).IsNull();
        await Assert.That(unset.AuthAudiences).IsEmpty();
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
