using System.Diagnostics;
using System.Globalization;
using System.Net;
using TerraBase.Configuration;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The internal listener records every peer credential check before admitting a matching request or refusing an unmatched one.
/// Cluster startup also refuses a missing or malformed secret.
/// </summary>
public sealed class PeerCredentialObservationTests
{
    private const string RequestsMetric = "terrabase_peer_authentication_requests_total";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private static CancellationToken Token => TestContext.Current!.Execution.CancellationToken;

    [Test]
    public async Task Internal_requests_are_recorded_by_credential_outcome_before_admission()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);
        using var scraper = new HttpClient { BaseAddress = server.MetricsAddress, Timeout = Deadline };
        using var raw = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = server.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var (matched, unmatched) = await CountsAsync(scraper);

        // The public listener is not an internal request, so it moves neither series.
        using (var health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), Token))
        {
            await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(await CountsAsync(scraper)).IsEqualTo((matched, unmatched));

        // Unmatched credentials are counted before refusal.
        var wrong = PeerTls.NewSecret();
        string[][] invalid =
        [
            [],
            [wrong],
            [PeerTls.Secret[..62] + "zz"],
            [PeerTls.Secret, PeerTls.Secret],
        ];
        foreach (var credentials in invalid)
        {
            await Assert.That(await ProbeAsync(raw, credentials)).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        var (invalidMatched, invalidUnmatched) = await CountsAsync(scraper);
        await Assert.That(invalidUnmatched).IsEqualTo(unmatched + invalid.Length);
        await Assert.That(invalidMatched).IsEqualTo(matched);

        // The generated secret matches.
        await Assert.That(await ProbeAsync(raw, [PeerTls.Secret])).IsEqualTo(HttpStatusCode.OK);

        var (validMatched, validUnmatched) = await CountsAsync(scraper);
        await Assert.That(validMatched).IsGreaterThan(matched);
        await Assert.That(validUnmatched).IsEqualTo(invalidUnmatched);
    }

    [Test]
    public async Task Server_entry_point_in_a_cluster_without_a_peer_secret_exits_2_before_touching_its_data_directory()
    {
        await AssertClusterStartupRefusedAsync(secret: null, echoed: null);
    }

    [Test]
    public async Task Server_entry_point_in_a_cluster_with_a_malformed_peer_secret_exits_2_without_echoing_it()
    {
        var malformed = $"{PeerTls.NewSecret()[..62]}zz";
        await AssertClusterStartupRefusedAsync(secret: malformed, echoed: malformed);
    }

    // The matched and unmatched sums of the scrape, read without credentials. An absent series counts as zero.
    internal static async Task<(double Matched, double Unmatched)> CountsAsync(HttpClient scraper)
    {
        var text = await scraper.GetStringAsync(new Uri("/metrics", UriKind.Relative), Token);
        return (Sum(text, "matched"), Sum(text, "unmatched"));

        static double Sum(string text, string outcome)
        {
            return text.Split('\n')
                .Where(line => line.StartsWith(RequestsMetric + "{", StringComparison.Ordinal)
                    && line.Contains($"credential=\"{outcome}\"", StringComparison.Ordinal))
                .Sum(line => double.Parse(line[(line.LastIndexOf('}') + 1)..].Trim().Split(' ')[0], CultureInfo.InvariantCulture));
        }
    }

    // Sends one GET /healthz with each value as a separate credential header field. The HTTP/2 client joins them into one field.
    private static async Task<HttpStatusCode> ProbeAsync(HttpClient raw, string[] credentials)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/healthz", UriKind.Relative))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        foreach (var value in credentials)
        {
            request.Headers.Add(PeerCredential.HeaderName, value);
        }

        using var response = await raw.SendAsync(request, Token);
        return response.StatusCode;
    }

    // The server entry point in a cluster with a missing or malformed secret: exit 2 naming the setting, nothing on stdout, and an
    // empty data directory, because the secret is read before the directory is opened.
    private static async Task AssertClusterStartupRefusedAsync(string? secret, string? echoed)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-peer-cluster-").FullName;
        try
        {
            var environment = ClusterIdentity();
            if (secret is not null)
            {
                environment[PeerCredential.EnvironmentVariable] = secret;
            }

            using var process = StartBinary(directory, environment);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(Deadline);
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var error = process.StandardError.ReadToEndAsync(deadline.Token);
                await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));

                await Assert.That(process.ExitCode).IsEqualTo(2);
                await Assert.That(await output).IsEmpty();
                await Assert.That(await error).Contains(PeerCredential.EnvironmentVariable);
                if (echoed is not null)
                {
                    await Assert.That(await error).DoesNotContain(echoed).Because("the malformed secret is never echoed");
                }

                await Assert.That(Directory.EnumerateFileSystemEntries(directory)).IsEmpty();
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Starts this build's server DLL with the production entry point. Ports are ephemeral, the data directory is the given one, and every other
    // setting is passed empty; inherited TS_*, TerraBase and AWS variables are removed so only the given environment applies.
    private static Process StartBinary(string directory, IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".dll"));
        foreach (var name in TerraBaseOptions.SettingNames)
        {
            var value = name switch
            {
                "port" or "internal-port" or "metrics-port" or "postgres-port" => "0",
                "data-directory" => directory,
                _ => string.Empty,
            };
            start.ArgumentList.Add($"--{name}={value}");
        }

        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TS_", StringComparison.Ordinal)
            || name.StartsWith("TERRABASE_", StringComparison.Ordinal) || name.StartsWith("AWS_", StringComparison.Ordinal)
            || name is "BUCKET_NAME" or "PRIMARY_REGION").ToList())
        {
            _ = start.Environment.Remove(name);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        return Process.Start(start) ?? throw new InvalidOperationException("the TerraBase process did not start");
    }

    // ts-orch's identity of a production cluster node, so the cluster checks apply.
    private static Dictionary<string, string> ClusterIdentity() => new(StringComparer.Ordinal)
    {
        ["TS_REGION"] = "gru",
        ["TS_NODE"] = "ts-gru-1",
        ["TS_PEERS_FILE"] = "/run/ts-orch/peers/peers",
        ["ASPNETCORE_ENVIRONMENT"] = "Production",
        ["DOTNET_ENVIRONMENT"] = "Production",
    };
}
