using System.Diagnostics;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Platform;

public sealed class ConfigurationTests
{
    [Test]
    [Arguments("master-key", "TS_AUTH_MASTER_KEY is required")]
    [Arguments("operator-key", "TS_AUTH_OPERATOR_KEY is required")]
    [Arguments("public-origin", "TS_AUTH_PUBLIC_ORIGIN is required")]
    [Arguments("database", "TS_AUTH_DATABASE is required")]
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
    [Arguments("http://127.0.0.1:8420", "Production auth storage cannot use a loopback TerraScale API URL")]
    [Arguments("https://evil.example", "Global auth storage requires https://api.terrascale.tech or https://gru.api.terrascale.tech")]
    [Arguments("http://api.terrascale.tech", "Global auth storage requires https://api.terrascale.tech or https://gru.api.terrascale.tech")]
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
    [Arguments("rate-limit", "0", "TS_AUTH_RATE_LIMIT must be positive")]
    public async Task Startup_rejects_malformed_settings(string key, string value, string message)
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values => values[key] = value));

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
    [Arguments("https://api.terrascale.tech")]
    [Arguments("https://gru.api.terrascale.tech")]
    public async Task Production_starts_with_either_permitted_storage_host(string apiUrl)
    {
        var settings = AuthTestServer.DefaultSettings();
        settings.Remove("environment");
        settings["terrascale-api-url"] = apiUrl;
        settings["terrascale-api-key"] = "Bearer  sk_test_production_start";
        await using var service = await RunningBinary.StartAsync(settings);

        using var health = await service.Client.GetAsync("/healthz");
        using var ready = await service.Client.GetAsync("/readyz");

        await Assert.That(health.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
        // No real authority is reachable from tests; readiness must still fail closed.
        await Assert.That(ready.StatusCode).IsEqualTo(System.Net.HttpStatusCode.ServiceUnavailable);
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

        public static async Task<RunningBinary> StartAsync(Dictionary<string, string> settings, params string[] arguments)
        {
            // Port 0: the child binds a free port itself and reports it on its startup line,
            // so no other process can answer in its place.
            settings["listen"] = "http://127.0.0.1:0";
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

            start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
            var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = Process.Start(start)!;
            process.OutputDataReceived += (_, line) =>
            {
                if (line.Data?.IndexOf("Now listening on: ", StringComparison.Ordinal) is >= 0 and var at)
                {
                    listening.TrySetResult(new Uri(line.Data[(at + "Now listening on: ".Length)..].Trim()));
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
