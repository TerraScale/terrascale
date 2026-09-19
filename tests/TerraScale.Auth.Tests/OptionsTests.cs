namespace TerraScale.Auth.Tests;

[NotInParallel]
public sealed class OptionsTests
{
    /// <summary>Snapshots every TS_AUTH_* variable: these tests clear or overwrite the
    /// block AuthOptions boots from, and must hand back what the fixtures set.</summary>
    private static IReadOnlyDictionary<string, string?> SnapshotAuthEnvironment() =>
        Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(k => k.StartsWith("TS_AUTH_", StringComparison.Ordinal))
            .ToDictionary(k => k, k => Environment.GetEnvironmentVariable(k));

    private static void RestoreAuthEnvironment(IReadOnlyDictionary<string, string?> snapshot)
    {
        foreach (var (name, value) in snapshot)
            Environment.SetEnvironmentVariable(name, value);
    }

    [Test]
    public async Task RequiredVariablesThrowActionableErrors()
    {
        var snapshot = SnapshotAuthEnvironment();
        try
        {
            foreach (var name in snapshot.Keys)
                Environment.SetEnvironmentVariable(name, null);
            var missing = Assert.Throws<InvalidOperationException>(() => TerraScale.Auth.Configuration.AuthOptions.Load([]));
            await Assert.That(missing.Message).Contains("TS_AUTH_PUBLIC_ORIGIN");
        }
        finally
        {
            RestoreAuthEnvironment(snapshot);
        }
    }

    [Test]
    public async Task OversizedMasterKeyIsRejectedAtLoad()
    {
        // 48 bytes decodes fine but is not a valid AES-GCM key size; accepting it
        // would crash every SecretBox call at runtime instead of failing at startup.
        Assert.Throws<InvalidOperationException>(() => TerraScale.Auth.Configuration.AuthOptions.Load(
        [
            "--public-origin=https://example.com",
            "--terrascale-api-key=k",
            "--database=ts-auth",
            "--master-key=" + Convert.ToBase64String(new byte[48]),
            "--operator-key=op",
        ]));
    }

    [Test]
    public async Task ArgsOverrideEnvironment()
    {
        var snapshot = SnapshotAuthEnvironment();
        try
        {
            Environment.SetEnvironmentVariable("TS_AUTH_PUBLIC_ORIGIN", "https://env.example");
            var options = TerraScale.Auth.Configuration.AuthOptions.Load(
            [
                "--public-origin=https://args.example",
                "--terrascale-api-key=k",
                "--database=ts-auth",
                "--master-key=" + Convert.ToBase64String(new byte[32]),
                "--operator-key=op",
            ]);
            await Assert.That(options.PublicOrigin.Host).IsEqualTo("args.example");
        }
        finally
        {
            RestoreAuthEnvironment(snapshot);
        }
    }

    [Test]
    public async Task EnvironmentUsedWhenNoArgGiven()
    {
        var snapshot = SnapshotAuthEnvironment();
        try
        {
            Environment.SetEnvironmentVariable("TS_AUTH_PUBLIC_ORIGIN", "https://env.example");
            var options = TerraScale.Auth.Configuration.AuthOptions.Load(
            [
                "--terrascale-api-key=k",
                "--database=ts-auth",
                "--master-key=" + Convert.ToBase64String(new byte[32]),
                "--operator-key=op",
            ]);
            await Assert.That(options.PublicOrigin.Host).IsEqualTo("env.example");
        }
        finally
        {
            RestoreAuthEnvironment(snapshot);
        }
    }

    [Test]
    public async Task DocumentedDefaultsFlowWhenEnvUnset()
    {
        var snapshot = SnapshotAuthEnvironment();
        try
        {
            foreach (var name in snapshot.Keys)
                Environment.SetEnvironmentVariable(name, null);
            var options = TerraScale.Auth.Configuration.AuthOptions.Load(
            [
                "--public-origin=https://example.com",
                "--terrascale-api-key=k",
                "--database=ts-auth",
                "--master-key=" + Convert.ToBase64String(new byte[32]),
                "--operator-key=op",
            ]);
            await Assert.That(options.Listen).IsEqualTo("http://+:8080");
            await Assert.That(options.RateLimitPerWindow).IsEqualTo(1000);
            await Assert.That(options.TerrascaleApiUrl.AbsoluteUri).IsEqualTo("https://api.terrascale.tech/");
            await Assert.That(options.StripeApiUrl.AbsoluteUri).IsEqualTo("https://api.stripe.com/");
        }
        finally
        {
            RestoreAuthEnvironment(snapshot);
        }
    }

    [Test]
    public async Task BadUrlOptionsThrowNamingTheVariable()
    {
        // Every URL option must reject unparseable, relative, and non-http(s) values
        // with an error naming its variable — not a bare UriFormatException (which
        // can't be told apart across four URL settings) and not silent acceptance
        // (an ftp:// origin would corrupt issuers, emails and redirects at runtime).
        foreach (var (arg, variable) in new[]
        {
            ("--public-origin=not a url", "TS_AUTH_PUBLIC_ORIGIN"),
            ("--public-origin=example.com", "TS_AUTH_PUBLIC_ORIGIN"),
            ("--public-origin=ftp://example.com", "TS_AUTH_PUBLIC_ORIGIN"),
            ("--terrascale-api-url=not a url", "TS_AUTH_TERRASCALE_API_URL"),
            ("--terrascale-api-url=example.com", "TS_AUTH_TERRASCALE_API_URL"),
            ("--terrascale-api-url=ftp://example.com", "TS_AUTH_TERRASCALE_API_URL"),
            ("--stripe-api-url=not a url", "TS_AUTH_STRIPE_API_URL"),
            ("--stripe-api-url=example.com", "TS_AUTH_STRIPE_API_URL"),
            ("--stripe-api-url=ftp://example.com", "TS_AUTH_STRIPE_API_URL"),
        })
        {
            var error = Assert.Throws<InvalidOperationException>(() => TerraScale.Auth.Configuration.AuthOptions.Load(
            [
                "--public-origin=https://example.com",
                "--terrascale-api-key=k",
                "--database=ts-auth",
                "--master-key=" + Convert.ToBase64String(new byte[32]),
                "--operator-key=op",
                arg,
            ]));
            await Assert.That(error.Message).Contains(variable);
        }
    }

    [Test]
    public async Task BlankOptionalUrlsFallBackToTheirDefaults()
    {
        foreach (var blank in new[] { "", " " })
        {
            var options = TerraScale.Auth.Configuration.AuthOptions.Load(
            [
                "--public-origin=https://example.com",
                $"--terrascale-api-url={blank}",
                $"--stripe-api-url={blank}",
                "--terrascale-api-key=k",
                "--database=ts-auth",
                "--master-key=" + Convert.ToBase64String(new byte[32]),
                "--operator-key=op",
            ]);
            await Assert.That(options.TerrascaleApiUrl.AbsoluteUri).IsEqualTo("https://api.terrascale.tech/");
            await Assert.That(options.StripeApiUrl.AbsoluteUri).IsEqualTo("https://api.stripe.com/");
        }
    }

    [Test]
    public async Task BlankListenAndBlankRequiredValuesThrowNamingTheVariable()
    {
        foreach (var (arg, variable) in new[]
        {
            ("--listen=", "TS_AUTH_LISTEN"),
            ("--listen=ftp://x:1", "TS_AUTH_LISTEN"),
            ("--listen=example.com", "TS_AUTH_LISTEN"),
            ("--public-origin=", "TS_AUTH_PUBLIC_ORIGIN"),
            ("--database=  ", "TS_AUTH_DATABASE"),
            ("--operator-key=", "TS_AUTH_OPERATOR_KEY"),
            ("--terrascale-api-key=", "TS_AUTH_TERRASCALE_API_KEY"),
        })
        {
            var error = Assert.Throws<InvalidOperationException>(() => TerraScale.Auth.Configuration.AuthOptions.Load(
            [
                "--public-origin=https://example.com",
                "--terrascale-api-key=k",
                "--database=ts-auth",
                "--master-key=" + Convert.ToBase64String(new byte[32]),
                "--operator-key=op",
                arg,
            ]));
            await Assert.That(error.Message).Contains(variable);
        }
    }

    [Test]
    public async Task MasterKeyPoisonsAreRejectedWithNamedErrors()
    {
        foreach (var poison in new[]
        {
            "not base64",
            // base64url variant: 0xFF bytes encode to "/..." so the swap is real
            Convert.ToBase64String(Enumerable.Repeat((byte)255, 32).ToArray()).Replace('+', '-').Replace('/', '_'),
            Convert.ToBase64String(new byte[16]), // too short for AES-256
            Convert.ToBase64String(new byte[33]), // not a valid AES-GCM key size
        })
        {
            var error = Assert.Throws<InvalidOperationException>(() => TerraScale.Auth.Configuration.AuthOptions.Load(
            [
                "--public-origin=https://example.com",
                "--terrascale-api-key=k",
                "--database=ts-auth",
                "--master-key=" + poison,
                "--operator-key=op",
            ]));
            await Assert.That(error.Message).Contains("TS_AUTH_MASTER_KEY");
        }
    }

    [Test]
    public async Task InvalidRateLimitThrowsInsteadOfSilentlyWidening()
    {
        // Garbage or non-positive limits must fail loudly: silently falling back to
        // 1000 would quietly loosen an operator's stricter intended limit.
        foreach (var poison in new[] { "abc", "0", "-5", "5,000", "999999999999" })
        {
            var error = Assert.Throws<InvalidOperationException>(() => TerraScale.Auth.Configuration.AuthOptions.Load(
            [
                "--public-origin=https://example.com",
                "--terrascale-api-key=k",
                "--database=ts-auth",
                "--master-key=" + Convert.ToBase64String(new byte[32]),
                "--operator-key=op",
                "--rate-limit=" + poison,
            ]));
            await Assert.That(error.Message).Contains("TS_AUTH_RATE_LIMIT");
        }

        var options = TerraScale.Auth.Configuration.AuthOptions.Load(
        [
            "--public-origin=https://example.com",
            "--terrascale-api-key=k",
            "--database=ts-auth",
            "--master-key=" + Convert.ToBase64String(new byte[32]),
            "--operator-key=op",
            "--rate-limit=250",
        ]);
        await Assert.That(options.RateLimitPerWindow).IsEqualTo(250);
    }

    [Test]
    public async Task UnknownOrMalformedArgsThrow()
    {
        // A typo'd or space-form option must not silently lose to the environment.
        foreach (var arg in new[] { "--public-orgin=https://x", "--typo=1", "--public-origin" })
        {
            var error = Assert.Throws<InvalidOperationException>(() => TerraScale.Auth.Configuration.AuthOptions.Load(
            [
                arg,
                "--public-origin=https://example.com",
                "--terrascale-api-key=k",
                "--database=ts-auth",
                "--master-key=" + Convert.ToBase64String(new byte[32]),
                "--operator-key=op",
            ]));
            await Assert.That(error.Message).Contains(arg);
        }
    }

    [Test]
    public async Task EveryOptionIsAcceptedAsAnArg()
    {
        // Pins the arg allowlist to the option set: adding an option without
        // allowing its arg form fails here with "Unknown or malformed option".
        var options = TerraScale.Auth.Configuration.AuthOptions.Load(
        [
            "--listen=http://+:9090",
            "--public-origin=https://example.com",
            "--terrascale-api-url=http://127.0.0.1:8420",
            "--terrascale-api-key=k",
            "--terrascale-project-id=proj",
            "--terrascale-tenant-id=ten",
            "--database=ts-auth",
            "--master-key=" + Convert.ToBase64String(new byte[32]),
            "--operator-key=op",
            "--rate-limit=50",
            "--stripe-secret-key=sk",
            "--stripe-webhook-secret=whsec",
            "--stripe-api-url=http://127.0.0.1:9",
            "--price-pro=price_pro",
            "--price-enterprise=price_ent",
        ]);
        await Assert.That(options.Listen).IsEqualTo("http://+:9090");
        await Assert.That(options.TerrascaleProjectId).IsEqualTo("proj");
        await Assert.That(options.RateLimitPerWindow).IsEqualTo(50);
    }

    [Test]
    public async Task RepeatedArgsLastWinsAndEqualsInValueSurvives()
    {
        var options = TerraScale.Auth.Configuration.AuthOptions.Load(
        [
            "--public-origin=https://example.com",
            "--terrascale-api-key=k",
            "--database=ts-auth",
            "--master-key=" + Convert.ToBase64String(new byte[32]),
            "--operator-key=op",
            "--listen=http://+:1",
            "--listen=http://+:2",
            "--stripe-api-url=http://127.0.0.1:9/a?b=c=d",
        ]);
        await Assert.That(options.Listen).IsEqualTo("http://+:2");
        await Assert.That(options.StripeApiUrl.PathAndQuery).IsEqualTo("/a?b=c=d");
    }

    [Test]
    public async Task BlankOptionalStringsAreTreatedAsUnset()
    {
        // "" must behave like unset: billing guards on `is null`, so an empty
        // string key would send an empty Bearer to Stripe instead of a 503.
        var options = TerraScale.Auth.Configuration.AuthOptions.Load(
        [
            "--public-origin=https://example.com",
            "--terrascale-api-key=k",
            "--database=ts-auth",
            "--master-key=" + Convert.ToBase64String(new byte[32]),
            "--operator-key=op",
            "--terrascale-project-id=",
            "--stripe-secret-key=",
            "--stripe-webhook-secret= ",
            "--price-pro=",
            "--price-enterprise= ",
        ]);
        await Assert.That(options.TerrascaleProjectId).IsNull();
        await Assert.That(options.StripeSecretKey).IsNull();
        await Assert.That(options.StripeWebhookSecret).IsNull();
        await Assert.That(options.PricePro).IsNull();
        await Assert.That(options.PriceEnterprise).IsNull();
    }
}
