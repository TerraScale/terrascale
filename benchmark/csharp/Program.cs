using System.Globalization;
using System.Net;
using System.Text.Json;
using TerraScale.Benchmark;
using TerraScale.DotNet;
using TerraScale.DotNet.Errors;
using TerraScale.DotNet.TerraBase;

var options = BenchmarkArgs.Parse(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var report = await Runner.RunAsync(options, cts.Token);
Console.WriteLine(JsonSerializer.Serialize(report, BenchmarkJsonContext.Default.SdkReport));
return 0;

internal sealed record BenchmarkArgs(
    Uri TerraBaseUrl,
    Uri AuthUrl,
    Uri DashboardUrl,
    string ApiKey,
    string Project,
    int Iterations,
    int Warmup,
    int BatchSize)
{
    public static BenchmarkArgs Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }

            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                values[arg[2..eq]] = arg[(eq + 1)..];
            }
            else if (i + 1 < args.Length)
            {
                values[arg[2..]] = args[++i];
            }
            else
            {
                throw new ArgumentException($"Missing value for '{arg}'.");
            }
        }

        string Required(string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"--{name} is required.");

        int IntOption(string name, int fallback) =>
            values.TryGetValue(name, out var raw)
                ? int.Parse(raw, CultureInfo.InvariantCulture)
                : fallback;

        return new BenchmarkArgs(
            new Uri(Required("terrabase-url").TrimEnd('/'), UriKind.Absolute),
            new Uri(Required("auth-url").TrimEnd('/'), UriKind.Absolute),
            new Uri(Required("dashboard-url").TrimEnd('/'), UriKind.Absolute),
            Required("api-key"),
            Required("project"),
            IntOption("iterations", 50),
            IntOption("warmup", 5),
            IntOption("batch-size", 25));
    }
}

internal static class Runner
{
    public static async Task<SdkReport> RunAsync(BenchmarkArgs options, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(TerraBaseHttp.CreateHandler());
        using var authHttp = new HttpClient(TerraBaseHttp.CreateHandler());
        using var dashHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });

        var auth = new TerraScaleAuthClient(authHttp, new TerraScaleAuthClientOptions
        {
            BaseAddress = options.AuthUrl,
            InstanceSlug = "terrascale",
        });

        var sqlClient = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions
        {
            BaseAddress = options.TerraBaseUrl,
            AuthAddress = options.AuthUrl,
            ApiKey = options.ApiKey,
        });

        var keyId = options.ApiKey.StartsWith("sk_", StringComparison.Ordinal)
            ? options.ApiKey[3..29]
            : options.ApiKey[..26];

        var runTag = new TerraScaleIdGenerator().NewId()[18..].ToLowerInvariant();
        List<ScenarioResult> results = [];

        async Task<string> AccessTokenAsync(CancellationToken ct) =>
            await auth.Oidc.RequestClientCredentialsTokenAsync(keyId, options.ApiKey, project: options.Project, cancellationToken: ct) switch
            {
                TerraScaleAuthTokenResponse token => token.AccessToken,
                AuthRefused refused => throw new InvalidOperationException($"Token exchange refused: {refused.ErrorCode} {refused.Message}"),
            };

        results.Add(await MeasureAsync("auth_token_exchange", options.Warmup, options.Iterations, async ct =>
        {
            await AccessTokenAsync(ct);
        }, cancellationToken));

        var lcCounter = 0;
        results.Add(await MeasureAsync("db_lifecycle", options.Warmup, options.Iterations, async ct =>
        {
            var index = ++lcCounter;
            var dbName = string.Create(CultureInfo.InvariantCulture, $"b-cs-lc-{runTag}-{index}");
            await sqlClient.CreateDatabaseAsync(options.Project, dbName, cancellationToken: ct);
            try
            {
                await sqlClient.Database(options.Project, dbName).ExecuteAsync(
                    "CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT NOT NULL, val INTEGER NOT NULL)",
                    cancellationToken: ct);
            }
            finally
            {
                await sqlClient.DeleteDatabaseAsync(options.Project, dbName, ct);
            }
        }, cancellationToken));

        var workDbName = $"b-cs-work-{runTag}";
        await sqlClient.CreateDatabaseAsync(options.Project, workDbName, cancellationToken: cancellationToken);
        try
        {
            var db = sqlClient.Database(options.Project, workDbName);
            await db.ExecuteAsync(
                "CREATE TABLE bench_items (id INTEGER PRIMARY KEY, tag TEXT NOT NULL, amount INTEGER NOT NULL)",
                cancellationToken: cancellationToken);

            var nextId = 1L;
            results.Add(await MeasureAsync("sql_rest_write", options.Warmup, options.Iterations, async ct =>
            {
                var id = nextId++;
                TerraBaseSqlValue[] parameters = [id, "rest", id * 10];
                await db.ExecuteAsync("INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)", parameters, ct);
            }, cancellationToken));

            var readMaxId = Math.Max(nextId - 1, 1L);
            var readCursor = 0L;
            results.Add(await MeasureAsync("sql_rest_read", options.Warmup, options.Iterations, async ct =>
            {
                var targetId = (readCursor++ % readMaxId) + 1;
                TerraBaseSqlValue[] parameters = [targetId];
                var rows = await db.QueryAsync("SELECT id, tag, amount FROM bench_items WHERE id = ?", parameters, ct);
                if (rows.Count != 1)
                {
                    throw new InvalidOperationException($"Expected 1 row for id {targetId}, got {rows.Count}.");
                }
            }, cancellationToken));

            results.Add(await MeasureAsync("sql_rest_batch", options.Warmup, options.Iterations, async ct =>
            {
                var statements = new TerraBaseSqlStatement[options.BatchSize];
                for (var i = 0; i < options.BatchSize; i++)
                {
                    var id = nextId++;
                    statements[i] = new TerraBaseSqlStatement(
                        "INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)",
                        [id, "batch", id * 10]);
                }

                var batchResults = await db.BatchAsync(statements, ct);
                if (batchResults.Count != options.BatchSize)
                {
                    throw new InvalidOperationException($"Expected {options.BatchSize} batch results, got {batchResults.Count}.");
                }
            }, cancellationToken));

            var consistency = new ConsistencyChannel(
                http,
                new Uri(options.TerraBaseUrl, $"/v1/projects/{Uri.EscapeDataString(options.Project)}/databases/{Uri.EscapeDataString(workDbName)}/sql/execute"),
                await AccessTokenAsync(cancellationToken));
            foreach (var mode in Enum.GetValues<ConsistencyMode>())
            {
                var wire = ConsistencyChannel.Wire(mode);
                results.Add(await MeasureAsync($"sql_{wire}_write", options.Warmup, options.Iterations, async ct =>
                {
                    var id = nextId++;
                    await consistency.ExecuteAsync(mode, "INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)", [id, wire, id * 10], causal: false, ct);
                }, cancellationToken));

                results.Add(await MeasureAsync($"sql_{wire}_bookmark_read", options.Warmup, options.Iterations, async ct =>
                {
                    var id = nextId++;
                    await consistency.ExecuteAsync(mode, "INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)", [id, wire, id * 10], causal: true, ct);
                    var read = await consistency.ExecuteAsync(mode, "SELECT id, tag, amount FROM bench_items WHERE id = ?", [id], causal: true, ct);
                    if (read.Rows != 1)
                    {
                        throw new InvalidOperationException($"Expected 1 row after {wire} bookmark read for id {id}, got {read.Rows}.");
                    }
                }, cancellationToken));
            }

            await using (var session = await db.ConnectAsync(cancellationToken))
            {
                results.Add(await MeasureAsync("sql_ws_transaction", options.Warmup, options.Iterations, async ct =>
                {
                    var id = nextId++;
                    await session.TransactionAsync(async (tx, token) =>
                    {
                        TerraBaseSqlValue[] writeParams = [id, "ws", id * 10];
                        await tx.ExecuteAsync("INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)", writeParams, token);
                        TerraBaseSqlValue[] readParams = [id];
                        var rows = await tx.QueryAsync("SELECT id, tag, amount FROM bench_items WHERE id = ?", readParams, token);
                        if (rows.Count != 1)
                        {
                            throw new InvalidOperationException($"Expected 1 row in ws tx, got {rows.Count}.");
                        }
                    }, ct);
                }, cancellationToken));
            }

            var healthLiveUri = new Uri(options.DashboardUrl, "/health/live");
            var oauthMetaUri = new Uri(options.DashboardUrl, "/.well-known/oauth-authorization-server");
            results.Add(await MeasureAsync("dashboard_roundtrip", options.Warmup, options.Iterations, async ct =>
            {
                using (var health = await dashHttp.GetAsync(healthLiveUri, ct))
                {
                    if (health.StatusCode != HttpStatusCode.OK)
                    {
                        throw new InvalidOperationException($"GET /health/live returned {(int)health.StatusCode}.");
                    }
                }

                using (var meta = await dashHttp.GetAsync(oauthMetaUri, ct))
                {
                    if (meta.StatusCode != HttpStatusCode.OK)
                    {
                        throw new InvalidOperationException($"GET /.well-known/oauth-authorization-server returned {(int)meta.StatusCode}.");
                    }
                }
            }, cancellationToken));
        }
        finally
        {
            await sqlClient.DeleteDatabaseAsync(options.Project, workDbName, CancellationToken.None);
        }

        return new SdkReport("csharp", results);
    }

    private static async Task<ScenarioResult> MeasureAsync(
        string name,
        int warmup,
        int iterations,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < warmup; i++)
        {
            await action(cancellationToken);
        }

        var samples = new double[iterations];
        var totalStarted = TimeProvider.System.GetTimestamp();
        for (var i = 0; i < iterations; i++)
        {
            var started = TimeProvider.System.GetTimestamp();
            await action(cancellationToken);
            samples[i] = TimeProvider.System.GetElapsedTime(started).TotalMilliseconds;
        }

        var totalSeconds = TimeProvider.System.GetElapsedTime(totalStarted).TotalSeconds;
        Array.Sort(samples);
        var sum = 0.0;
        foreach (var sample in samples)
        {
            sum += sample;
        }

        var opsPerSec = totalSeconds > 0 ? iterations / totalSeconds : 0.0;
        return new ScenarioResult(
            name,
            iterations,
            opsPerSec,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99),
            sum / samples.Length,
            samples[0],
            samples[^1]);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var idx = Math.Clamp((int)Math.Round((sorted.Length - 1) * p, MidpointRounding.AwayFromZero), 0, sorted.Length - 1);
        return sorted[idx];
    }
}
