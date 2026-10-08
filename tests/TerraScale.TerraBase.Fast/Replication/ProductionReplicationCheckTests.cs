using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Features.Management.Usage;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>The production checker as a subprocess against real client TLS, catalog replication and admin scrapes.</summary>
public sealed class ProductionReplicationCheckTests
{
    // Observation faults end the checker's host sampling at once and tolerate the other member's lag, so these
    // scenarios share one member pair and run together.
    private static readonly string[] ObservationFaults =
    [
        "missing-start", "missing-metrics-complete", "missing-logs-complete", "missing-initialized", "missing-durable-lag",
        "missing-durable-lag-first-sample", "missing-catalog-connected", "missing-catalog-lag", "malformed-metric", "negative-metric",
        "nan-metric", "infinite-metric", "host-exit", "shell-scrape-exit", "shell-journal-exit", "shell-two-processes",
        "shell-missing-process",
    ];

    private static readonly string[] InvalidConfigurations =
    [
        "empty-host", "blank-host", "empty-members", "duplicate-members", "invalid-member", "invalid-hostname", "too-many-members",
        "zero-lag", "negative-lag", "nan-lag", "infinite-lag",
    ];

    // A checker run has three convergence windows of --max-lag-seconds each (documents, listings, host lag). Test
    // members converge in well under a second, so these tests deliberately allow 5 s (they used 10 s, production
    // defaults to 15 s): a run that needs longer has stalled and fails rather than waits. A run also starts
    // interpreters, writes the canary and runs host commands, with up to 17 runs sharing one member pair.
    private const int MaxLagSeconds = 5;
    private static readonly TimeSpan CheckerRun = TimeSpan.FromSeconds((3 * MaxLagSeconds) + 15);

    // Server startup, provisioning and readiness share one budget, so a slow setup cannot take a checker run's time.
    private static readonly TimeSpan SetupBudget = TimeSpan.FromSeconds(45);

    // These need both members converged, and durable lag is server-wide, so each scenario owns its member pair.
    [Test]
    [Arguments("none")]
    [Arguments("shell")]
    [Arguments("reported-faults")]
    [Arguments("reported-faults-first-sample")]
    [Arguments("shell-reported-faults")]
    public async Task Checker_succeeds_only_with_complete_valid_observations_of_real_members(string scenario)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        // Setup, then one checker run and, for passing scenarios, a second.
        deadline.CancelAfter(SetupBudget + (2 * CheckerRun));
        await using var pair = new MemberPair();
        await pair.StartAsync([scenario], deadline.Token);
        await pair.RunAsync(scenario, repeat: false, deadline.Token);
        if (scenario is "none" or "shell")
        {
            await pair.RunAsync(scenario, repeat: true, deadline.Token);
        }
    }

    [Test]
    public async Task Checker_fails_on_incomplete_or_invalid_observations_of_real_members()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        // Setup, then one wave of concurrent checker runs.
        deadline.CancelAfter(SetupBudget + CheckerRun);
        await using var pair = new MemberPair();
        await pair.StartAsync(ObservationFaults, deadline.Token);
        await Task.WhenAll(ObservationFaults.Select(fault => pair.RunAsync(fault, repeat: false, deadline.Token)));
    }

    [Test]
    public async Task Invalid_checker_configuration_exits_before_creating_the_canary_collection()
    {
        // One server for every configuration, each with its own canary database and directory, under one deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        // Setup, then one wave of concurrent checker runs.
        deadline.CancelAfter(SetupBudget + CheckerRun);
        var token = deadline.Token;
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(token);
        setup.CancelAfter(SetupBudget);
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, cancellationToken: setup.Token);
        var faker = new Faker();
        var databases = InvalidConfigurations.ToDictionary(fault => fault, _ => "check-" + faker.Random.AlphaNumeric(8).ToLowerInvariant());
        var tenant = await ReplicationTenant.CreateAsync(server, [.. databases.Values], setup.Token);
        var secret = await tenant.KeyAsync(server, ["documents:read", "documents:write"], setup.Token);
        var member = server.ClientAddress!.Authority;
        var directory = Directory.CreateTempSubdirectory("terrabase-prod-check-invalid-").FullName;
        try
        {
            await Task.WhenAll(InvalidConfigurations.Select(ConfigurationAsync));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        async Task ConfigurationAsync(string fault)
        {
            string[] members = fault switch
            {
                "empty-members" => [],
                "duplicate-members" => [member, member],
                "invalid-member" => ["localhost:0"],
                "invalid-hostname" => ["localhost..:9465"],
                "too-many-members" => [.. Enumerable.Range(1, 101).Select(port => $"localhost:{port}")],
                _ => [member],
            };
            var lag = fault switch
            {
                "zero-lag" => "0",
                "negative-lag" => "-1",
                "nan-lag" => "NaN",
                "infinite-lag" => "inf",
                _ => "10",
            };
            var database = databases[fault];
            var canary = Path.Combine(Directory.CreateDirectory(Path.Combine(directory, fault)).FullName, "canary.json");
            await WriteCanaryAsync(canary, server, tls, tenant, database, secret, members, token);
            var command = fault switch { "empty-host" => string.Empty, "blank-host" => " \t ", _ => "exit 23" };
            var (exitCode, output, error) = await RunCheckerAsync(canary, tls, command, [], token, lag);
            var because = $"configuration {fault}:\n{output}{error}";
            await Assert.That(exitCode).IsEqualTo(2).Because(because);
            await Assert.That(output).DoesNotContain("OK:").Because(because);
            using var client = tls.CreateClient(server.ClientAddress!, secret);
            using var collection = await client.GetAsync(new Uri($"{tenant.Path(database)}/collections/canary/documents", UriKind.Relative), token);
            await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because(because);
            using var response = await client.GetAsync(new Uri($"{tenant.Path(database)}/collections", UriKind.Relative), token);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            await Assert.That(page.GetProperty("items").GetArrayLength()).IsEqualTo(0).Because(because);
        }
    }

    // TerraBaseTestServer.EventuallyAsync under the caller's setup token.
    private static async Task UntilAsync(Func<CancellationToken, Task<bool>> condition, CancellationToken token)
    {
        while (!await condition(token))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), token);
        }

        token.ThrowIfCancellationRequested();
    }

    private static async Task WriteCanaryAsync(string path, TerraBaseTestServer server, ClientTls tls, ReplicationTenant tenant,
        string database, string secret, string[] members, CancellationToken token)
    {
        var config = new JsonObject
        {
            ["api"] = server.ClientAddress!.ToString(),
            ["project"] = tenant.ProjectId,
            ["database"] = database,
            ["secret"] = secret,
            ["members"] = new JsonArray([.. members.Select(member => (JsonNode?)JsonValue.Create(member))]),
            ["member_ca"] = tls.CertificatePem,
        };
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(config.ToJsonString().AsMemory(), token);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCheckerAsync(string canary, ClientTls tls, string hostCommand,
        Dictionary<string, string> environment, CancellationToken token, string? lagSeconds = null)
    {
        var start = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        string[] arguments = [Path.Combine(AppContext.BaseDirectory, "scripts", "prod-replication-check"), "--canary", canary,
            "--docs", "4", "--host-exec", hostCommand, "--max-lag-seconds=" + (lagSeconds ?? MaxLagSeconds.ToString(CultureInfo.InvariantCulture))];
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Trust the real test certificate only in this child; never change the test process's TLS environment.
        var certificate = Path.Combine(Path.GetDirectoryName(canary)!, "trusted-client.pem");
        await File.WriteAllTextAsync(certificate, tls.CertificatePem, token);
        start.Environment["SSL_CERT_FILE"] = certificate;
        // Nix's OpenSSL (the CI builder's python3) reads this one before SSL_CERT_FILE.
        start.Environment["NIX_SSL_CERT_FILE"] = certificate;
        start.Environment["NO_PROXY"] = "localhost,127.0.0.1";
        start.Environment["no_proxy"] = "localhost,127.0.0.1";
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(CheckerRun);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("the production checker did not start");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"the checker for {canary} did not exit within its {CheckerRun.TotalSeconds:g} s budget", error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // Shared by both host command fakes: reads a member's real admin metrics.
    private const string MetricsReader = """
        import json, os, re, sys, time, urllib.request

        COUNTERS = ("terrabase_replication_received_groups_total", "terrabase_replication_sender_prefix_mismatches_total")


        # True when the member's real gauges show no replication lag, as the checker's lag() defines it.
        def converged(lines):
            values = {}
            for line in lines:
                name = re.split(r"[ {]", line, maxsplit=1)[0]
                if name.startswith(("terrabase_replication_sender_", "terrabase_catalog_follower_")):
                    values[name] = float(line.split()[1])
            return (values.get("terrabase_replication_sender_initialized") == 1 and values.get("terrabase_replication_sender_durable_lag") == 0
                    and values.get("terrabase_catalog_follower_connected", 1) == 1 and values.get("terrabase_catalog_follower_lag", 0) == 0)


        # One real scrape within 5 s. Reported-fault cases stop the checker at its first sample, so there a member's real
        # baseline is read again within the same 5 s until it is converged; the last real sample is returned either way.
        def scrape(url):
            baseline = "reported-faults" in os.environ["CHECKER_FAULT"]
            started = time.monotonic()
            while True:
                try:
                    with urllib.request.urlopen(url, timeout=max(0.1, 5 - (time.monotonic() - started))) as response:
                        lines = response.read(1048576).decode().splitlines()
                except Exception as error:
                    # The checker reports only the first 200 characters of stderr: say what failed before any traceback.
                    sys.exit(f"{type(error).__name__}: {error} reading {url} after {time.monotonic() - started:.1f}s")
                if not baseline or converged(lines) or time.monotonic() - started >= 5:
                    return lines
                time.sleep(0.1)
        """;

    private const string HostAdapter = MetricsReader + "\n" + """
        member = sys.argv[1]
        assert sys.argv[2] == member.rsplit(":", 1)[0], "{ip} must remain the member hostname"
        host = json.loads(os.environ["CHECKER_HOSTS"])[member]
        fault = os.environ["CHECKER_FAULT"] if member == os.environ["CHECKER_FAULT_MEMBER"] else "none"
        if os.environ["CHECKER_FAULT"].endswith("-first-sample"):
            path = os.path.join(os.environ["CHECKER_SAMPLES"], member)
            count = 0
            if os.path.exists(path):
                with open(path, encoding="utf-8") as file:
                    count = int(file.read())
            with open(path, "w", encoding="utf-8") as file:
                file.write(str(count + 1))
            if count > 0:
                fault = "none"
            elif os.environ["CHECKER_FAULT"] == "reported-faults-first-sample":
                fault = "lagging-reported-faults" if member == os.environ["CHECKER_FAULT_MEMBER"] else "none"
            else:
                fault = "missing-durable-lag" if member == os.environ["CHECKER_FAULT_MEMBER"] else "first-sample-lag"
        metrics = scrape(host["metrics"])
        with open(host["logs"], encoding="utf-8") as file:
            logs = file.read(65536)
        if fault == "host-exit":
            sys.exit(23)
        if fault != "missing-start":
            print("start " + os.environ["CHECKER_STARTED"])
        omitted = {
            "missing-initialized": "terrabase_replication_sender_initialized",
            "missing-durable-lag": "terrabase_replication_sender_durable_lag",
            "missing-catalog-connected": "terrabase_catalog_follower_connected",
            "missing-catalog-lag": "terrabase_catalog_follower_lag",
        }.get(fault)
        invalid = {"malformed-metric": "broken", "negative-metric": "-1", "nan-metric": "NaN", "infinite-metric": "+Inf"}.get(fault)
        for line in metrics:
            if not re.match(r"^terrabase_(replication_(sender_initialized|sender_durable_lag|received_groups_total|sender_prefix_mismatches_total)|catalog_follower_(connected|lag))[ {]", line):
                continue
            name = re.split(r"[ {]", line, maxsplit=1)[0]
            if name == omitted or (fault.endswith("reported-faults") and name in COUNTERS):
                continue
            if name == "terrabase_replication_sender_durable_lag":
                replacement = invalid or ("1" if fault in ("first-sample-lag", "lagging-reported-faults") else None)
                if replacement:
                    fields = line.split()
                    fields[1] = replacement
                    line = " ".join(fields)
            print("metric " + line)
        if fault.endswith("reported-faults"):
            print('metric terrabase_replication_received_groups_total{otel_scope_name="TerraScale.TerraBase",result="rejected"} 1')
            print('metric terrabase_replication_sender_prefix_mismatches_total{otel_scope_name="TerraScale.TerraBase"} 2')
        if fault != "missing-metrics-complete":
            print("complete metrics")
        print(logs, end="")
        if fault.endswith("reported-faults"):
            print("log warn: TerraBase.Engine.Sqlite.SqliteStorageEngine\tA journal group failed its check\tSystem.InvalidOperationException: a test fault")
            print("bad SQLite Error 11: 'database disk image is malformed'.")
        if fault != "missing-logs-complete":
            print("complete logs")
        """;

    // These commands exist only on the checker's PATH; bash receives the real production HOST_SCRIPT on stdin.
    private const string HostTools = "#!/usr/bin/env python3\n" + MetricsReader + "\n" + """
        tool = os.path.basename(sys.argv[0])
        member = os.environ["CHECKER_MEMBER"]
        host = json.loads(os.environ["CHECKER_HOSTS"])[member]
        fault = os.environ["CHECKER_FAULT"] if member == os.environ["CHECKER_FAULT_MEMBER"] else "none"
        if tool == "pgrep":
            assert sys.argv[1:] == ["-f", "/install/terrabase/"]
            # 4194305 is above the kernel's largest pid_max, so no process has it.
            print({"shell-two-processes": os.environ["CHECKER_PID"] + "\n1", "shell-missing-process": "4194305"}.get(fault, os.environ["CHECKER_PID"]))
        elif tool == "curl":
            assert sys.argv[-1] == "http://127.0.0.1:9464/metrics"
            lines = scrape(host["metrics"])
            if fault == "shell-reported-faults":
                lines = [line for line in lines if not line.startswith(COUNTERS)] + [
                    'terrabase_replication_received_groups_total{otel_scope_name="TerraScale.TerraBase",result="rejected"} 1',
                    'terrabase_replication_sender_prefix_mismatches_total{otel_scope_name="TerraScale.TerraBase"} 2']
            print("\n".join(lines))
            if fault == "shell-scrape-exit":
                sys.exit(22)
        elif tool == "sudo":
            assert sys.argv[1] == "journalctl"
            os.execvp("journalctl", sys.argv[1:])
        elif tool == "journalctl":
            with open(host["logs"], encoding="utf-8") as file:
                for line in file:
                    prefix, message, exception = line.rstrip("\n").split("\t", 2)
                    level, category = prefix.removeprefix("log ").split(": ", 1)
                    level = {"Warning": "warn", "Error": "fail", "Critical": "crit"}[level]
                    print(f"{level}: {category}\n{message}\n{exception}")
            if fault == "shell-reported-faults":
                # The corruption line follows the warning's three lines, as a separate journal entry.
                print("warn: TerraBase.Engine.Sqlite.SqliteStorageEngine\nA journal group failed its check\nSystem.InvalidOperationException: a test fault")
                print("SQLite Error 11: 'database disk image is malformed'.")
            if fault == "shell-journal-exit":
                sys.exit(23)
        else:
            raise AssertionError("unexpected host command: " + tool)
        """;

    // A primary and a follower with real TLS, catalog replication and captured logs, with one canary database per
    // scenario; every scenario runs the real checker against both members through the host command fakes.
    private sealed class MemberPair : IAsyncDisposable
    {
        private readonly PeerTls _peers = new();
        private readonly ClientTls _tls = new();
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-prod-check-").FullName;
        private readonly Dictionary<string, string> _databases = [with(StringComparer.Ordinal)];
        private readonly string[] _members = new string[2];
        private HostLogs? _primaryLogs;
        private HostLogs? _followerLogs;
        private TerraBaseTestServer? _primary;
        private TerraBaseTestServer? _follower;
        private ReplicationTenant? _tenant;
        private string _secret = string.Empty;
        private string _hosts = string.Empty;

        private string Adapter => Path.Combine(_directory, "host-adapter.py");

        private string Tools => Path.Combine(_directory, "bin");

        public async Task StartAsync(IReadOnlyList<string> scenarios, CancellationToken cancellationToken)
        {
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            setup.CancelAfter(SetupBudget);
            var token = setup.Token;
            var primaryPort = TerraBaseTestServer.FreePort();
            var followerPort = TerraBaseTestServer.FreePort();
            var primaryLog = Path.Combine(_directory, "primary.log");
            var followerLog = Path.Combine(_directory, "follower.log");
            _primaryLogs = new HostLogs(primaryLog);
            _followerLogs = new HostLogs(followerLog);
            void Configure(Dictionary<string, string> settings, string node, int port)
            {
                _peers.Apply(settings);
                _tls.Apply(settings);
                settings["peer-port"] = port.ToString(CultureInfo.InvariantCulture);
                settings["peers"] = $"primary=localhost:{primaryPort},follower=localhost:{followerPort}";
                settings["node-name"] = node;
                settings["catalog-primary"] = "primary";
            }

            static void ConfigureServices(IServiceCollection services, HostLogs logs)
            {
                services.AddSingleton<ILoggerProvider>(logs);
                // Usage has separate coverage; its periodic catalog writes would move the measured replication heads.
                foreach (var flush in services.Where(service => service.ImplementationType == typeof(UsageFlushService)).ToList())
                {
                    services.Remove(flush);
                }
            }

            _primary = await TerraBaseTestServer.StartAsync(
                settings => Configure(settings, "primary", primaryPort), services => ConfigureServices(services, _primaryLogs), cancellationToken: token);
            _follower = await TerraBaseTestServer.StartAsync(
                settings => Configure(settings, "follower", followerPort), services => ConfigureServices(services, _followerLogs), cancellationToken: token);
            var primary = _primary;
            var follower = _follower;
            var faker = new Faker();
            foreach (var scenario in scenarios)
            {
                _databases[scenario] = "check-" + faker.Random.AlphaNumeric(8).ToLowerInvariant();
            }

            var tenant = _tenant = await ReplicationTenant.CreateAsync(primary, [.. _databases.Values], token);
            _secret = await tenant.KeyAsync(primary, ["documents:read", "documents:write"], token);
            using var reader = _tls.CreateClient(follower.ClientAddress!, _secret);
            // No fixture catalog copying or second key: the follower must serve the primary's catalog and key.
            await UntilAsync(async cancellationToken =>
            {
                foreach (var database in _databases.Values)
                {
                    // The follower may not have a tenant data file until replication first opens it.
                    using var response = await reader.PutAsync(new Uri(tenant.Path(database), UriKind.Relative), null, cancellationToken);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return false;
                    }
                }

                return await follower.MetricAsync("terrabase_catalog_follower_connected", "", cancellationToken) == 1
                    && await follower.MetricAsync("terrabase_catalog_follower_lag", "", cancellationToken) == 0;
            }, token);
            // TerraBaseTestServer.ReplicatedAsync's condition, under this test's deadline.
            await UntilAsync(async cancellationToken =>
            {
                TerraBaseTestServer[] servers = [primary, follower];
                foreach (var server in servers)
                {
                    if (await server.MetricAsync("terrabase_replication_sender_initialized", "", cancellationToken) != 1
                        || await server.MetricAsync("terrabase_replication_sender_durable_lag", "", cancellationToken) != 0)
                    {
                        return false;
                    }
                }

                return true;
            }, token);
            // The observation window starts after bootstrap: neither member's tenant catalog existed before warmup.
            _primaryLogs.StartObservation();
            _followerLogs.StartObservation();

            _members[0] = primary.ClientAddress!.Authority;
            _members[1] = follower.ClientAddress!.Authority;
            await File.WriteAllTextAsync(Adapter, HostAdapter, token);
            Directory.CreateDirectory(Tools);
            string[] names = ["pgrep", "curl", "sudo", "journalctl"];
            foreach (var name in names)
            {
                var path = Path.Combine(Tools, name);
                await File.WriteAllTextAsync(path, HostTools, token);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            _hosts = new JsonObject
            {
                [_members[0]] = new JsonObject { ["metrics"] = new Uri(primary.BaseAddress, "/metrics").ToString(), ["logs"] = primaryLog },
                [_members[1]] = new JsonObject { ["metrics"] = new Uri(follower.BaseAddress, "/metrics").ToString(), ["logs"] = followerLog },
            }.ToJsonString();
        }

        // Runs one scenario's checker against both members and asserts what that fault must produce.
        public async Task RunAsync(string fault, bool repeat, CancellationToken token)
        {
            var primary = _primary!;
            var tenant = _tenant!;
            var members = _members;
            var database = _databases[fault];
            var scenario = Directory.CreateDirectory(Path.Combine(_directory, fault)).FullName;
            var canary = Path.Combine(scenario, "canary.json");
            if (!repeat)
            {
                await WriteCanaryAsync(canary, primary, _tls, tenant, database, _secret, members, token);
            }

            var samples = Directory.CreateDirectory(Path.Combine(scenario, "samples")).FullName;
            var temporary = Directory.CreateDirectory(Path.Combine(scenario, "tmp")).FullName;
            var environment = new Dictionary<string, string>
            {
                ["CHECKER_HOSTS"] = _hosts,
                ["CHECKER_STARTED"] = new DateTimeOffset(_process.StartTime.ToUniversalTime()).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ["CHECKER_FAULT"] = fault,
                ["CHECKER_FAULT_MEMBER"] = members[1],
                ["CHECKER_SAMPLES"] = samples,
                ["CHECKER_PID"] = _process.Id.ToString(CultureInfo.InvariantCulture),
                ["TMPDIR"] = temporary,
            };
            var command = $"python3 {ShellQuote(Adapter)} {{member}} {{ip}}";
            if (fault.StartsWith("shell", StringComparison.Ordinal))
            {
                environment["PATH"] = Tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                command = "env CHECKER_MEMBER={member} bash -s";
            }

            var (exitCode, output, error) = await RunCheckerAsync(canary, _tls, command, environment, token);
            var because = $"scenario {fault}{(repeat ? ", second run" : "")}:\n{output}{error}";
            if (repeat)
            {
                // A second run uses the existing documents' If-Match versions as well as conditional creates of tombstones.
                await Assert.That(exitCode).IsEqualTo(0).Because(because);
                await Assert.That(Directory.EnumerateFileSystemEntries(temporary)).IsEmpty().Because(because);
                await Assert.That(output).Contains("OK: sampled canary state and host observations agree on run").Because(because);
                await Assert.That(output).Contains("wrote: 3 live, 1 deleted").Because(because);
                TerraBaseTestServer[] servers = [primary, _follower!];
                foreach (var server in servers)
                {
                    using var client = _tls.CreateClient(server.ClientAddress!, _secret);
                    using var response = await client.GetAsync(new Uri($"{tenant.Path(database)}/collections/canary/documents", UriKind.Relative), token);
                    response.EnsureSuccessStatusCode();
                    var page = await response.Content.ReadFromJsonAsync<JsonElement>(token);
                    var items = page.GetProperty("items").EnumerateArray().ToArray();
                    await Assert.That(items.Length).IsEqualTo(3).Because(fault);
                    await Assert.That(items.Select(item => item.GetProperty("document").GetProperty("index").GetInt32()))
                        .IsEquivalentTo(Enumerable.Range(0, 3)).Because(fault);
                    await Assert.That(items.Count(item => item.GetProperty("document").TryGetProperty("patched", out _))).IsEqualTo(1).Because(fault);
                    await Assert.That(items.Any(item => item.GetProperty("document").TryGetProperty("stale", out _))).IsFalse().Because(fault);
                }

                return;
            }

            var succeeds = fault is "none" or "shell";
            await Assert.That(exitCode).IsEqualTo(succeeds ? 0 : 1).Because(because);
            await Assert.That(Directory.EnumerateFileSystemEntries(temporary)).IsEmpty().Because(because);
            await Assert.That(output).Contains("wrote: 3 live, 1 deleted").Because(because);
            foreach (var member in members)
            {
                await Assert.That(output.Split('\n').Any(line => line.StartsWith(member + " ", StringComparison.Ordinal)
                    && line.Contains("4/4", StringComparison.Ordinal))).IsTrue().Because(because);
            }

            if (fault.Contains("reported-faults", StringComparison.Ordinal))
            {
                // Complete, valid observations whose content reports a fault: each signal fails the run by itself.
                string[] reported =
                [
                    "terrabase_replication_received_groups_total{result=\"rejected\"} = 1 since the process started",
                    "terrabase_replication_sender_prefix_mismatches_total = 2 since the process started",
                    "log (1x in 20 min): warn: TerraBase.Engine.Sqlite.SqliteStorageEngine A journal group failed its check"
                        + " System.InvalidOperationException: a test fault",
                    "corruption sign in the log: SQLite Error 11: 'database disk image is malformed'.",
                ];
                foreach (var line in reported)
                {
                    await Assert.That(output).Contains($"FAIL {members[1]}: {line}").Because(because);
                }

                await Assert.That(output).DoesNotContain("host checks failed").Because(because);
                await Assert.That(output).DoesNotContain($"FAIL {members[0]}:").Because(because);
                await Assert.That(output).DoesNotContain("OK:").Because(because);
                if (fault == "reported-faults-first-sample")
                {
                    // The faulty sample also lagged; a retry's healthy sample must not replace it.
                    await Assert.That(output).Contains($"FAIL {members[1]}: replication not converged:").Because(because);
                    foreach (var member in members)
                    {
                        await Assert.That(await File.ReadAllTextAsync(Path.Combine(samples, member), token)).IsEqualTo("1").Because(because);
                    }
                }

                return;
            }

            if (!succeeds)
            {
                await Assert.That(output).Contains($"FAIL {members[1]}: host checks failed:").Because(because);
                // Observation errors stop polling immediately, so the other member may still report real lag.
                await Assert.That(output).DoesNotContain($"FAIL {members[0]}: host checks failed:").Because(because);
                await Assert.That(output).DoesNotContain("OK:").Because(because);
                if (fault == "missing-durable-lag-first-sample")
                {
                    await Assert.That(output).Contains("host metrics missing terrabase_replication_sender_durable_lag").Because(because);
                    await Assert.That(output).Contains($"FAIL {members[0]}: replication not converged:").Because(because);
                    foreach (var member in members)
                    {
                        await Assert.That(await File.ReadAllTextAsync(Path.Combine(samples, member), token)).IsEqualTo("1").Because(because);
                    }
                }

                if (fault is "shell-scrape-exit" or "shell-journal-exit")
                {
                    await Assert.That(output).Contains("host command exited " + (fault == "shell-scrape-exit" ? "22" : "23")).Because(because);
                }

                if (fault == "shell-two-processes")
                {
                    await Assert.That(output).Contains($"host command exited 3: expected one TerraBase process, found: {_process.Id} 1")
                        .Because(because);
                }

                if (fault == "shell-missing-process")
                {
                    await Assert.That(output).Contains("host command exited 1: stat: ").Because(because);
                }

                return;
            }

            await Assert.That(output).Contains("OK: sampled canary state and host observations agree on run").Because(because);
        }

        // As nested using scopes would: every resource is released even when an earlier disposal throws.
        public async ValueTask DisposeAsync()
        {
            try
            {
                try
                {
                    if (_follower is not null)
                    {
                        await _follower.DisposeAsync();
                    }
                }
                finally
                {
                    if (_primary is not null)
                    {
                        await _primary.DisposeAsync();
                    }
                }
            }
            finally
            {
                _followerLogs?.Dispose();
                _primaryLogs?.Dispose();
                _process.Dispose();
                try
                {
                    _tls.Dispose();
                }
                finally
                {
                    try
                    {
                        _peers.Dispose();
                    }
                    finally
                    {
                        Directory.Delete(_directory, recursive: true);
                    }
                }
            }
        }
    }

    // Capture the same warning categories the production adapter reads, without manufacturing an empty log report.
    // Every change replaces the file by rename, so the checker's host commands read whole snapshots, never a partial record.
    private sealed class HostLogs : ILoggerProvider
    {
        private readonly string _path;
        private readonly Lock _gate = new();
        private readonly StringBuilder _lines = new();
        private int _bytes;

        public HostLogs(string path)
        {
            _path = path;
            Publish();
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void StartObservation()
        {
            lock (_gate)
            {
                _lines.Clear();
                _bytes = 0;
                Publish();
            }
        }

        public void Dispose()
        {
        }

        private void Append(string line)
        {
            lock (_gate)
            {
                var bytes = Encoding.UTF8.GetByteCount(line);
                if (_bytes + bytes > 65536)
                {
                    throw new InvalidOperationException("the checker test's captured logs exceeded 64 KiB");
                }

                _lines.Append(line);
                _bytes += bytes;
                Publish();
            }
        }

        private void Publish()
        {
            var next = _path + ".next";
            File.WriteAllText(next, _lines.ToString());
            File.Move(next, _path, overwrite: true);
        }

        private sealed class Logger(HostLogs logs, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning
                && (category.StartsWith("TerraBase.Features.Replication", StringComparison.Ordinal)
                    || category.StartsWith("TerraBase.Features.Catalog", StringComparison.Ordinal)
                    || category.StartsWith("TerraBase.Engine", StringComparison.Ordinal));

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    var message = formatter(state, exception).Replace('\r', ' ').Replace('\n', ' ');
                    var firstException = exception?.ToString().Split('\n')[0].TrimEnd('\r');
                    logs.Append($"log {logLevel}: {category}\t{message}\t{firstException}\n");
                }
            }
        }
    }
}
