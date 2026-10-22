using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraBase.E2E;

/// <summary>
/// The disposable ts-orch service a live run targets (docs/terrabase/e2e-ts-orch.md): <see cref="Service"/>, one node per
/// region, each reached directly at its client listener over NetBird. Every request carries a ts-auth project token minted
/// from the e2e project's API key. A region stops when the service's own spec is applied without it, and starts again
/// when the spec is applied with every region; nothing else of ts-orch is touched.
/// </summary>
internal static class Orch
{
    public const string NodesVariable = "TERRABASE_E2E_ORCH_NODES";
    public const string KeyVariable = "TERRABASE_E2E_ORCH_KEY";
    public const string Service = "terrabase-e2e";

    // Any ts-orch host runs the CLI (apply is global); gru's host stays up while its terrabase-e2e node is removed.
    private const string Host = "ts-gru-1";
    private const string Issuer = "https://auth.terrascale.tech/terrascale";
    private const string Scopes = "databases:read databases:write documents:read documents:write";
    private static readonly string[] Regions = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];
    private static readonly Uri Healthz = new("/healthz", UriKind.Relative);
    private static readonly Lock Gate = new();
    private static Task<(string Token, DateTimeOffset Renew)>? _minted;
    private static Task<JsonObject>? _spec;

    // The regions this run has stopped: a second stop keeps the first one down, and a start brings back only its own region.
    private static readonly HashSet<string> Stopped = [];

    /// <summary>Each region's client listener, from <c>gru=http://ip:port,sjc=...,fra=...</c>; every one of <paramref name="regions"/> once.</summary>
    public static Dictionary<string, Uri> Nodes(Func<string, string?> variable, IReadOnlyList<string> regions)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(regions);
        var nodes = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var entry in (variable(NodesVariable) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Split('=') is not [{ Length: > 0 } region, var address] || !Uri.TryCreate(address, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttp || url.AbsolutePath != "/" || !nodes.TryAdd(region, url))
            {
                throw new InvalidOperationException($"{NodesVariable} must list each region once as region=http://<netbird-ip>:<port>/; got {entry}");
            }
        }

        var missing = regions.Where(region => !nodes.ContainsKey(region)).ToArray();
        return missing.Length == 0 ? nodes : throw new InvalidOperationException($"{NodesVariable} names no node for {string.Join(", ", missing)}");
    }

    /// <summary>A client of one node: the run's bearer on every request, and a refused connection answered as 503, as a stopped local node is.</summary>
    public static HttpClient Node(Uri address) =>
        new(new NodeHandler { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false } }) { BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>The run's ts-auth project token: minted from <see cref="KeyVariable"/> and renewed two minutes before it expires.</summary>
    public static async Task<string> TokenAsync()
    {
        Task<(string Token, DateTimeOffset Renew)> minted;
        lock (Gate)
        {
            if (_minted is null || _minted.IsFaulted || (_minted.IsCompletedSuccessfully && _minted.Result.Renew < DateTimeOffset.UtcNow))
            {
                _minted = MintAsync();
            }

            minted = _minted;
        }

        return (await minted).Token;
    }

    /// <summary>The project and organization the token acts in (its <c>project_id</c> and <c>org_id</c> claims).</summary>
    public static async Task<(string Project, string Organization)> ClaimsAsync()
    {
        var payload = (await TokenAsync()).Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=')));
        return (claims.RootElement.GetProperty("project_id").GetString()!, claims.RootElement.GetProperty("org_id").GetString()!);
    }

    /// <summary>Removes <paramref name="region"/> from the service and waits until its node refuses connections.</summary>
    public static async Task StopAsync(string region, HttpClient node, CancellationToken token)
    {
        await ApplyAsync(await SpecAsync(node.BaseAddress!.Port), region, stop: true, token);
        _ = await Converge.UntilAsync(() => HealthAsync(node, token), status => status == HttpStatusCode.ServiceUnavailable,
            $"{Service} in {region} stopped", TimeSpan.FromMinutes(2), token);
    }

    /// <summary>Applies the service with every region again and waits until <paramref name="region"/>'s node answers as itself.</summary>
    public static async Task StartAsync(string region, HttpClient node, CancellationToken token)
    {
        await ApplyAsync(await SpecAsync(node.BaseAddress!.Port), region, stop: false, token);
        _ = await Converge.UntilAsync(() => HealthAsync(node, token), status => status == HttpStatusCode.OK,
            $"{Service} in {region} serving again", TimeSpan.FromMinutes(4), token);
    }

    private static async Task<HttpStatusCode> HealthAsync(HttpClient node, CancellationToken token)
    {
        try
        {
            using var health = await node.GetAsync(Healthz, token);
            return health.StatusCode;
        }
        catch (HttpRequestException error) when (error.StatusCode is { } status)
        {
            return status;
        }
    }

    // The service's spec as it was first read in this run: it must be the disposable service, in every region, on the
    // nodes' port, so a stop can never be applied to anything else or to a half-stopped spec.
    private static Task<JsonObject> SpecAsync(int port)
    {
        lock (Gate)
        {
            _spec ??= ReadSpecAsync();
        }

        return CheckedAsync(_spec, port);

        static async Task<JsonObject> CheckedAsync(Task<JsonObject> read, int port)
        {
            var spec = await read;
            return spec["port"]?.GetValue<int>() == port ? spec : throw new InvalidOperationException($"{Service} serves port {spec["port"]}, not the nodes' {port}");
        }

        static async Task<JsonObject> ReadSpecAsync()
        {
            var spec = JsonNode.Parse(await SshAsync($"sudo -n ts-orch service inspect {Service}", "", CancellationToken.None))?["spec"]?.AsObject()
                ?? throw new InvalidOperationException($"ts-orch service inspect {Service} answered no spec");
            var regions = spec["regions"]?.AsArray().Select(region => region?.GetValue<string>()).Order(StringComparer.Ordinal).ToArray() ?? [];
            return spec["name"]?.GetValue<string>() == Service && regions.SequenceEqual(Regions.Order(StringComparer.Ordinal))
                ? spec
                : throw new InvalidOperationException($"{Service} must run in exactly {string.Join(", ", Regions)} before an outage run; it runs in [{string.Join(", ", regions)}]");
        }
    }

    // The spec travels on stdin into a private temporary file on the host: never in an argument or a log.
    private static Task<string> ApplyAsync(JsonObject spec, string region, bool stop, CancellationToken token)
    {
        var applied = spec.DeepClone().AsObject();
        lock (Gate)
        {
            _ = stop ? Stopped.Add(region) : Stopped.Remove(region);
            applied["regions"] = new JsonArray([.. Regions.Where(serving => !Stopped.Contains(serving)).Select(serving => JsonValue.Create(serving))]);
        }

        return SshAsync("f=$(mktemp) && cat > \"$f\" && sudo -n ts-orch service apply -f \"$f\"; s=$?; rm -f \"$f\"; exit $s", applied.ToJsonString(), token);
    }

    // A canceled token kills ssh; what ts-orch was already asked to apply may still happen.
    private static async Task<string> SshAsync(string command, string input, CancellationToken token)
    {
        var start = new ProcessStartInfo("ssh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in (string[])["-o", "BatchMode=yes", Host, command])
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("ssh did not start");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.StandardInput.WriteAsync(input.AsMemory(), token);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return process.ExitCode == 0 ? await output : throw new InvalidOperationException($"ssh {Host} {command} exited {process.ExitCode}: {await error}");
    }

    // ts-auth's client credentials grant for a project API key (docs/auth/platform-contract.md, 6.1): the key's ULID is its
    // client id and the whole key its secret. A refusal names its status only, never the key or the answer's body.
    private static async Task<(string Token, DateTimeOffset Renew)> MintAsync()
    {
        var key = Environment.GetEnvironmentVariable(KeyVariable) is { Length: > 27 } value
            ? value
            : throw new InvalidOperationException($"{KeyVariable} must hold the e2e project's ts-auth API key to run the suite against ts-orch");
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Issuer}/oauth/token"))
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("scope", Scopes)]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key[..26]}:{key}")));
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ts-auth refused the {KeyVariable} client credentials exchange: HTTP {(int)response.StatusCode}");
        }

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (body.RootElement.GetProperty("access_token").GetString()!,
            DateTimeOffset.UtcNow.AddSeconds(body.RootElement.GetProperty("expires_in").GetInt32() - 120));
    }

    private sealed class NodeHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException error) when (error.StatusCode is null)
            {
                throw new HttpRequestException($"{request.RequestUri} is unreachable: {error.Message}", error, HttpStatusCode.ServiceUnavailable);
            }
        }
    }
}
