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
    [Test]
    [Arguments("none")]
    [Arguments("missing-start")]
    [Arguments("missing-metrics-complete")]
    [Arguments("missing-logs-complete")]
    [Arguments("missing-initialized")]
    [Arguments("missing-durable-lag")]
    [Arguments("missing-durable-lag-first-sample")]
    [Arguments("missing-catalog-connected")]
    [Arguments("missing-catalog-lag")]
    [Arguments("malformed-metric")]
    [Arguments("negative-metric")]
    [Arguments("nan-metric")]
    [Arguments("infinite-metric")]
    [Arguments("host-exit")]
    [Arguments("reported-faults")]
    [Arguments("reported-faults-first-sample")]
    [Arguments("shell")]
    [Arguments("shell-scrape-exit")]
    [Arguments("shell-journal-exit")]
    [Arguments("shell-two-processes")]
    [Arguments("shell-missing-process")]
    [Arguments("shell-reported-faults")]
    public async Task Checker_succeeds_only_with_complete_valid_observations_of_real_members(string fault)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        var directory = Directory.CreateTempSubdirectory("terrabase-prod-check-").FullName;
        try
        {
            var primaryPort = TerraBaseTestServer.FreePort();
            var followerPort = TerraBaseTestServer.FreePort();
            var primaryLog = Path.Combine(directory, "primary.log");
            var followerLog = Path.Combine(directory, "follower.log");
            using var primaryLogs = new HostLogs(primaryLog);
            using var followerLogs = new HostLogs(followerLog);
            void Configure(Dictionary<string, string> settings, string node, int port)
            {
                peers.Apply(settings);
                tls.Apply(settings);
                settings["peer-port"] = port.ToString(CultureInfo.InvariantCulture);
                settings["peers"] = $"primary=localhost:{primaryPort},follower=localhost:{followerPort}";
                settings["node-name"] = node;
                settings["catalog-primary"] = "primary";
            }

            void ConfigureServices(IServiceCollection services, HostLogs logs)
            {
                services.AddSingleton<ILoggerProvider>(logs);
                // Usage has separate coverage; its periodic catalog writes would move the measured replication heads.
                foreach (var flush in services.Where(service => service.ImplementationType == typeof(UsageFlushService)).ToList())
                {
                    services.Remove(flush);
                }
            }

            await using var primary = await TerraBaseTestServer.StartAsync(
                settings => Configure(settings, "primary", primaryPort), services => ConfigureServices(services, primaryLogs));
            await using var follower = await TerraBaseTestServer.StartAsync(
                settings => Configure(settings, "follower", followerPort), services => ConfigureServices(services, followerLogs));
            var database = "check-" + new Faker().Random.AlphaNumeric(8).ToLowerInvariant();
            var tenant = await ReplicationTenant.CreateAsync(primary, database);
            var secret = await tenant.KeyAsync(primary, "documents:read", "documents:write");
            using var reader = tls.CreateClient(follower.ClientAddress!, secret);
            var databasePath = new Uri(tenant.Path(database), UriKind.Relative);
            // No fixture catalog copying or second key: the follower must serve the primary's catalog and key.
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
            {
                // The follower may not have a tenant data file until replication first opens it.
                using var response = await reader.PutAsync(databasePath, null, cancellationToken);
                return response.StatusCode == HttpStatusCode.OK
                    && await follower.MetricAsync("terrabase_catalog_follower_connected", "", cancellationToken) == 1
                    && await follower.MetricAsync("terrabase_catalog_follower_lag", "", cancellationToken) == 0;
            });
            await TerraBaseTestServer.ReplicatedAsync(primary, follower);
            // The observation window starts after bootstrap: neither member's tenant catalog existed before warmup.
            primaryLogs.StartObservation();
            followerLogs.StartObservation();

            string[] members = [primary.ClientAddress!.Authority, follower.ClientAddress!.Authority];
            var canary = Path.Combine(directory, "canary.json");
            await WriteCanaryAsync(canary, primary, tls, tenant, database, secret, members, token);
            var adapter = Path.Combine(directory, "host-adapter.py");
            await File.WriteAllTextAsync(adapter, HostAdapter, token);
            var samples = Directory.CreateDirectory(Path.Combine(directory, "samples")).FullName;
            var temporary = Directory.CreateDirectory(Path.Combine(directory, "tmp")).FullName;
            var hosts = new JsonObject
            {
                [members[0]] = new JsonObject { ["metrics"] = new Uri(primary.BaseAddress, "/metrics").ToString(), ["logs"] = primaryLog },
                [members[1]] = new JsonObject { ["metrics"] = new Uri(follower.BaseAddress, "/metrics").ToString(), ["logs"] = followerLog },
            };
            using var currentProcess = Process.GetCurrentProcess();
            var environment = new Dictionary<string, string>
            {
                ["CHECKER_HOSTS"] = hosts.ToJsonString(),
                ["CHECKER_STARTED"] = new DateTimeOffset(currentProcess.StartTime.ToUniversalTime()).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ["CHECKER_FAULT"] = fault,
                ["CHECKER_FAULT_MEMBER"] = members[1],
                ["CHECKER_SAMPLES"] = samples,
                ["CHECKER_PID"] = currentProcess.Id.ToString(CultureInfo.InvariantCulture),
                ["TMPDIR"] = temporary,
            };
            var command = $"python3 {ShellQuote(adapter)} {{member}} {{ip}}";
            if (fault.StartsWith("shell", StringComparison.Ordinal))
            {
                var tools = Directory.CreateDirectory(Path.Combine(directory, "bin")).FullName;
                string[] names = ["pgrep", "curl", "sudo", "journalctl"];
                foreach (var name in names)
                {
                    var path = Path.Combine(tools, name);
                    await File.WriteAllTextAsync(path, HostTools, token);
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                }

                environment["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                command = "env CHECKER_MEMBER={member} bash -s";
            }

            var succeeds = fault is "none" or "shell";
            var (exitCode, output, error) = await RunAsync(canary, tls, command, environment, token);
            await Assert.That(exitCode).IsEqualTo(succeeds ? 0 : 1).Because(output + error);
            await Assert.That(Directory.EnumerateFileSystemEntries(temporary)).IsEmpty();
            await Assert.That(output).Contains("wrote: 3 live, 1 deleted");
            foreach (var member in members)
            {
                await Assert.That(output.Split('\n').Any(line => line.StartsWith(member + " ", StringComparison.Ordinal)
                    && line.Contains("4/4", StringComparison.Ordinal))).IsTrue().Because(output);
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
                    await Assert.That(output).Contains($"FAIL {members[1]}: {line}");
                }

                await Assert.That(output).DoesNotContain("host checks failed");
                await Assert.That(output).DoesNotContain($"FAIL {members[0]}:");
                await Assert.That(output).DoesNotContain("OK:");
                if (fault == "reported-faults-first-sample")
                {
                    // The faulty sample also lagged; a retry's healthy sample must not replace it.
                    await Assert.That(output).Contains($"FAIL {members[1]}: replication not converged:");
                    foreach (var member in members)
                    {
                        await Assert.That(await File.ReadAllTextAsync(Path.Combine(samples, member), token)).IsEqualTo("1");
                    }
                }

                return;
            }

            if (!succeeds)
            {
                await Assert.That(output).Contains($"FAIL {members[1]}: host checks failed:");
                // Observation errors stop polling immediately, so the other member may still report real lag.
                await Assert.That(output).DoesNotContain($"FAIL {members[0]}: host checks failed:");
                await Assert.That(output).DoesNotContain("OK:");
                if (fault == "missing-durable-lag-first-sample")
                {
                    await Assert.That(output).Contains("host metrics missing terrabase_replication_sender_durable_lag");
                    await Assert.That(output).Contains($"FAIL {members[0]}: replication not converged:");
                    foreach (var member in members)
                    {
                        await Assert.That(await File.ReadAllTextAsync(Path.Combine(samples, member), token)).IsEqualTo("1");
                    }
                }

                if (fault is "shell-scrape-exit" or "shell-journal-exit")
                {
                    await Assert.That(output).Contains("host command exited " + (fault == "shell-scrape-exit" ? "22" : "23"));
                }

                if (fault == "shell-two-processes")
                {
                    await Assert.That(output).Contains($"host command exited 3: expected one TerraBase process, found: {currentProcess.Id} 1");
                }

                if (fault == "shell-missing-process")
                {
                    await Assert.That(output).Contains("host command exited 1: stat: ");
                }

                return;
            }

            await Assert.That(output).Contains("OK: sampled canary state and host observations agree on run");
            // A second run uses the existing documents' If-Match versions as well as conditional creates of tombstones.
            var (repeatExitCode, repeatOutput, repeatError) = await RunAsync(canary, tls, command, environment, token);
            await Assert.That(repeatExitCode).IsEqualTo(0).Because(repeatOutput + repeatError);
            await Assert.That(Directory.EnumerateFileSystemEntries(temporary)).IsEmpty();
            await Assert.That(repeatOutput).Contains("OK: sampled canary state and host observations agree on run");
            await Assert.That(repeatOutput).Contains("wrote: 3 live, 1 deleted");
            TerraBaseTestServer[] servers = [primary, follower];
            foreach (var server in servers)
            {
                using var client = tls.CreateClient(server.ClientAddress!, secret);
                using var response = await client.GetAsync(new Uri($"{tenant.Path(database)}/collections/canary/documents", UriKind.Relative), token);
                response.EnsureSuccessStatusCode();
                var page = await response.Content.ReadFromJsonAsync<JsonElement>(token);
                var items = page.GetProperty("items").EnumerateArray().ToArray();
                await Assert.That(items.Length).IsEqualTo(3);
                await Assert.That(items.Select(item => item.GetProperty("document").GetProperty("index").GetInt32()))
                    .IsEquivalentTo(Enumerable.Range(0, 3));
                await Assert.That(items.Count(item => item.GetProperty("document").TryGetProperty("patched", out _))).IsEqualTo(1);
                await Assert.That(items.Any(item => item.GetProperty("document").TryGetProperty("stale", out _))).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments("empty-host")]
    [Arguments("blank-host")]
    [Arguments("empty-members")]
    [Arguments("duplicate-members")]
    [Arguments("invalid-member")]
    [Arguments("invalid-hostname")]
    [Arguments("too-many-members")]
    [Arguments("zero-lag")]
    [Arguments("negative-lag")]
    [Arguments("nan-lag")]
    [Arguments("infinite-lag")]
    public async Task Invalid_checker_configuration_exits_before_creating_the_canary_collection(string fault)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        var database = "check-" + new Faker().Random.AlphaNumeric(8).ToLowerInvariant();
        var tenant = await ReplicationTenant.CreateAsync(server, database);
        var secret = await tenant.KeyAsync(server, "documents:read", "documents:write");
        var member = server.ClientAddress!.Authority;
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
        var directory = Directory.CreateTempSubdirectory("terrabase-prod-check-invalid-").FullName;
        try
        {
            var canary = Path.Combine(directory, "canary.json");
            await WriteCanaryAsync(canary, server, tls, tenant, database, secret, members, token);
            var command = fault switch { "empty-host" => string.Empty, "blank-host" => " \t ", _ => "exit 23" };
            var (exitCode, output, error) = await RunAsync(canary, tls, command, [], token, lag);
            await Assert.That(exitCode).IsEqualTo(2).Because(output + error);
            await Assert.That(output).DoesNotContain("OK:");
            using var client = tls.CreateClient(server.ClientAddress!, secret);
            using var collection = await client.GetAsync(new Uri($"{tenant.Path(database)}/collections/canary/documents", UriKind.Relative), token);
            await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            using var response = await client.GetAsync(new Uri($"{tenant.Path(database)}/collections", UriKind.Relative), token);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            await Assert.That(page.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string canary, ClientTls tls, string hostCommand,
        Dictionary<string, string> environment, CancellationToken token, string lagSeconds = "10")
    {
        var start = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        string[] arguments = [Path.Combine(AppContext.BaseDirectory, "scripts", "prod-replication-check"), "--canary", canary,
            "--docs", "4", "--host-exec", hostCommand, "--max-lag-seconds=" + lagSeconds];
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
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("the production checker did not start");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            return (process.ExitCode, await output, await error);
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
