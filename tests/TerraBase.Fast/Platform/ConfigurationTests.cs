using System.Net;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class ConfigurationTests
{
    [Test]
    public async Task A_node_takes_its_identity_from_orch_and_a_process_without_injection_is_local()
    {
        const string ChildFlag = "TERRABASE_TEST_ORCH_IDENTITY_CHILD";
        if (Environment.GetEnvironmentVariable(ChildFlag) != "1")
        {
            // Identity variables are process-wide: only a process that runs this test alone may set them.
            await IsolatedTestChild.RunAsync(typeof(ConfigurationTests), nameof(A_node_takes_its_identity_from_orch_and_a_process_without_injection_is_local), ChildFlag);
            return;
        }

        Environment.SetEnvironmentVariable("TS_REGION", null);
        Environment.SetEnvironmentVariable("TS_NODE_ID", null);
        var local = TerraBaseOptions.FromValues(new Dictionary<string, string>());
        await Assert.That((local.Region, local.MachineId)).IsEqualTo(("local", "local"));
        // Production always trusts the platform's tokens; Development trusts none unless set.
        var production = TerraBaseOptions.FromValues(new Dictionary<string, string>(), production: true);
        await Assert.That((production.AuthIssuer, production.AuthAudiences.Single())).IsEqualTo((TerraBaseOptions.PlatformAuthIssuer, TerraBaseOptions.PlatformAuthAudience));
        await Assert.That(local.AuthIssuer).IsNull();

        Environment.SetEnvironmentVariable("TS_REGION", "gru");
        foreach (var nodeId in new[] { "01ARZ3NDEKTSV4RRFFQ69G5FAV", "01arz3ndektsv4rrffq69g5fav" })
        {
            Environment.SetEnvironmentVariable("TS_NODE_ID", nodeId);
            var orch = TerraBaseOptions.FromValues(new Dictionary<string, string>());
            await Assert.That((orch.Region, orch.MachineId)).IsEqualTo(("gru", nodeId));
            await Assert.That(Listeners.InternalAddress(orch)).IsEqualTo(IPAddress.Loopback);
        }

        // Partial and malformed identities never start, and errors name settings without echoing values.
        foreach (var nodeId in new[]
        {
            "Not A Node", "d894655b3ed118", "81ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FÁV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAI", "01ARZ3NDEKTSV4RRFFQ69G5FA-", " 01ARZ3NDEKTSV4RRFFQ69G5FAV", null,
        })
        {
            Environment.SetEnvironmentVariable("TS_NODE_ID", nodeId);
            var malformed = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string>())).Throws<TerraBaseConfigurationException>();
            await Assert.That(malformed!.Message).Contains("TS_NODE_ID");
            await Assert.That(malformed.Message).DoesNotContain("gru");
            if (nodeId is not null)
            {
                await Assert.That(malformed.Message).DoesNotContain(nodeId);
            }
        }

        Environment.SetEnvironmentVariable("TS_REGION", null);
        Environment.SetEnvironmentVariable("TS_NODE_ID", "01ARZ3NDEKTSV4RRFFQ69G5FAV");
        var partial = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string>())).Throws<TerraBaseConfigurationException>();
        await Assert.That(partial!.Message).Contains("TS_REGION");
        await Assert.That(partial.Message).DoesNotContain("01ARZ3NDEKTSV4RRFFQ69G5FAV");
    }

    [Test]
    public async Task Production_trusts_only_the_one_platform_issuer_and_audience_ts_auth_issues_in_every_region()
    {
        const string ChildFlag = "TERRABASE_TEST_AUTH_PAIR_CHILD";
        if (Environment.GetEnvironmentVariable(ChildFlag) != "1")
        {
            // Identity variables are process-wide: only a process that runs this test alone may set them.
            await IsolatedTestChild.RunAsync(typeof(ConfigurationTests), nameof(Production_trusts_only_the_one_platform_issuer_and_audience_ts_auth_issues_in_every_region), ChildFlag);
            return;
        }

        static Dictionary<string, string> Auth(string issuer, string audience)
        {
            return new() { ["auth-issuer"] = issuer, ["auth-audience"] = audience };
        }

        Environment.SetEnvironmentVariable("TS_REGION", null);
        Environment.SetEnvironmentVariable("TS_NODE_ID", null);

        // ts-auth's issuer and TerraBase's audience have no region: the same token is accepted in gru, sjc and fra.
        var production = TerraBaseOptions.FromValues(Auth(TerraBaseOptions.PlatformAuthIssuer, TerraBaseOptions.PlatformAuthAudience), production: true);
        await Assert.That((production.AuthIssuer, production.AuthAudiences.Single())).IsEqualTo(("https://auth.terrascale.tech/terrascale", "https://api.terrascale.tech"));
        Environment.SetEnvironmentVariable("TS_REGION", "gru");
        Environment.SetEnvironmentVariable("TS_NODE_ID", "01ARZ3NDEKTSV4RRFFQ69G5FAV");
        var defaults = TerraBaseOptions.FromValues(new Dictionary<string, string>(), production: true);
        await Assert.That((defaults.AuthIssuer, defaults.AuthAudiences.Single())).IsEqualTo((production.AuthIssuer, production.AuthAudiences.Single()));
        foreach (var (issuer, audience) in new[]
        {
            ("https://gru.auth.terrascale.tech/terrascale", TerraBaseOptions.PlatformAuthAudience),
            ("https://auth.terrascale.tech", TerraBaseOptions.PlatformAuthAudience),
            (TerraBaseOptions.PlatformAuthIssuer, "https://gru.api.terrascale.tech"),
        })
        {
            var refused = await Assert.That(() => TerraBaseOptions.FromValues(Auth(issuer, audience), production: true)).Throws<TerraBaseConfigurationException>();
            await Assert.That(refused!.Message).Contains("auth-issuer must be https://auth.terrascale.tech/terrascale");
        }

        // An explicit test environment can run its own ts-auth under its own origin.
        var test = TerraBaseOptions.FromValues(Auth(TestIssuer.Issuer, TestIssuer.Audience), production: false);
        await Assert.That((test.AuthIssuer, test.AuthAudiences.Single())).IsEqualTo((TestIssuer.Issuer, TestIssuer.Audience));
    }

    [Test]
    public async Task Partial_backup_settings_name_a_missing_one()
    {
        var partialBackup = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { ["backup-bucket"] = "gru-1-backup" }))
            .Throws<TerraBaseConfigurationException>();
        await Assert.That(partialBackup!.Message).Contains("master-key");
    }

    [Test]
    public async Task Backup_retention_defaults_to_one_seven_and_thirty_days_and_takes_plan_overrides()
    {
        var s3 = new FakeS3();
        var values = new Dictionary<string, string>();
        s3.Configure(values);
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
