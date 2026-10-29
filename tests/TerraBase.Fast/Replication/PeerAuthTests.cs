using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;
using ReplicationRpc = TerraBase.Features.Replication.V1.Replication;
using WireDatabase = TerraBase.Features.Replication.V1.Database;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The internal listener requires the shared peer credential on every request (C1). Each test sends a credential the
/// server must refuse, through raw clients with no default credential, and checks the refusal arrives as HTTP 401 before any
/// payload is read: the same payloads with the valid credential fail their own validation instead.
/// </summary>
public sealed class PeerAuthTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly string[] ReadScopes = ["documents:read"];

    private static CancellationToken Token => TestContext.Current!.Execution.CancellationToken;

    // Credential values the server must refuse. The HTTP/2 client comma-joins repeated values into one field,
    // including repetitions of the valid credential.
    private static (string Case, string[] Values)[] RefusedCredentials()
    {
        var wrong = PeerTls.NewSecret();
        return
        [
            ("missing", []),
            ("wrong", [wrong]),
            ("comma-joined-wrong", [wrong, wrong]),
            ("comma-joined-correct", [PeerTls.Secret, PeerTls.Secret]),
            ("comma-joined-mixed", [PeerTls.Secret, wrong]),
            ("short", [new string('a', 63)]),
            ("long", [new string('a', 65)]),
            ("non-hex", [new string('z', 64)]),
            ("empty", [string.Empty]),
            ("leading-space", [" " + new string('a', 63)]),
        ];
    }

    // The payloads name no valid database, so a request that reaches validation fails with InvalidArgument.
    private static WireDatabase InvalidDatabase()
    {
        var database = ChangeGroups.Wire(new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString()), null);
        database.DatabaseId = "not-a-ulid";
        return database;
    }

    [Test]
    public async Task Every_peer_rpc_refuses_bad_credentials_before_payload_validation()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var invalid = InvalidDatabase();
        foreach (var (name, values) in RefusedCredentials())
        {
            using var channel = RawChannel(server.BaseAddress, values);
            var client = new ReplicationRpc.ReplicationClient(channel);
            await Assert.That(await StatusOf(async () => await PushHelloAndBatchAsync(client, invalid)))
                .IsEqualTo(StatusCode.Unauthenticated).Because($"{name} Push");
            await Assert.That(await StatusOf(async () => _ = await client.DigestAsync(new DigestRequest { Database = invalid }, cancellationToken: Token)))
                .IsEqualTo(StatusCode.Unauthenticated).Because($"{name} Digest");
            await Assert.That(await StatusOf(async () => await ReadBucketFirstPageAsync(client, invalid)))
                .IsEqualTo(StatusCode.Unauthenticated).Because($"{name} ReadBucket");
            await Assert.That(await StatusOf(async () => await ReadSnapshotFirstPageAsync(client, invalid)))
                .IsEqualTo(StatusCode.Unauthenticated).Because($"{name} ReadSnapshot");
        }
    }

    [Test]
    public async Task Comma_joined_valid_credentials_are_refused()
    {
        var observed = new CredentialHeaderCount();
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<IStartupFilter>(observed));
        using var http = RawHttp(server.BaseAddress, [PeerTls.Secret, PeerTls.Secret]);
        using var response = await http.GetAsync(new Uri("/peer-auth-duplicate", UriKind.Relative), Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await observed.Count.Task.WaitAsync(Token)).IsEqualTo(1)
            .Because("this HTTP/2 client comma-joins the two values into one field");
    }

    [Test]
    public async Task With_the_valid_credential_the_same_payloads_fail_their_own_validation()
    {
        // Proves the refusal tests above would notice a payload that reached validation: these payloads are invalid.
        await using var server = await TerraBaseTestServer.StartAsync();
        var invalid = InvalidDatabase();
        using var channel = PeerChannel.Create(server.BaseAddress, PeerTls.Credential);
        var client = new ReplicationRpc.ReplicationClient(channel);

        await Assert.That(await StatusOf(async () => await PushHelloAndBatchAsync(client, invalid))).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(await StatusOf(async () => _ = await client.DigestAsync(new DigestRequest { Database = invalid }, cancellationToken: Token)))
            .IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(await StatusOf(async () => await ReadBucketFirstPageAsync(client, invalid))).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(await StatusOf(async () => await ReadSnapshotFirstPageAsync(client, invalid))).IsEqualTo(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task Internal_probes_and_administration_refuse_every_bad_credential_with_401()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var before = await CountsAsync(server);
        var owner = EntityIds.New().ToString();
        foreach (var (name, values) in RefusedCredentials())
        {
            using var http = RawHttp(server.BaseAddress, values);
            foreach (var path in new[] { "/healthz", "/health/replication", "/v1/catalog-gate", "/no/such/route" })
            {
                using var response = await http.GetAsync(new Uri(path, UriKind.Relative), Token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized).Because($"{name} {path}");
            }

            // Key issue and a management commit that claims an operator's user ID, which the credential must still gate.
            using var key = await http.PostAsync(new Uri("/admin/api-keys", UriKind.Relative), KeyBody(EntityIds.New().ToString()), Token);
            await Assert.That(key.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized).Because($"{name} POST /admin/api-keys");
            using var commit = CommitRequest(owner, EntityIds.New().ToString());
            using var refused = await http.SendAsync(commit, Token);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized).Because($"{name} forged management commit");
        }

        // The refused requests wrote no organization API key and no organization.
        await Assert.That(await CountsAsync(server)).IsEqualTo(before);

        // The same commit with the valid credential is accepted, so the refusals above are the credential's.
        using var accepted = await server.Client.SendAsync(CommitRequest(owner, EntityIds.New().ToString()), Token);
        await Assert.That(accepted.IsSuccessStatusCode).IsTrue();

        // The same administration path answers the valid credential, so the refusal is the credential's.
        using var health = await server.Client.GetAsync(new Uri("/health/replication", UriKind.Relative), Token);
        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Public_health_answers_without_a_peer_credential()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var http = new HttpClient { BaseAddress = server.ClientAddress };
        using var response = await http.GetAsync(new Uri("/healthz", UriKind.Relative), Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_forged_push_with_a_members_node_id_is_refused_and_the_legitimate_streams_stay_connected()
    {
        var members = new FakeMemberStore();
        var gru = new Region("gru", members);
        var sjc = new Region("sjc", members);
        try
        {
            await gru.StartAsync();
            await sjc.StartAsync();

            // Legitimate streams first: a catalog write from each region reaches the other.
            var before = await gru.Api.CreateOrganizationAsync("Before forgery");
            await sjc.UntilOrganizationAsync(before);
            var sjcBefore = await sjc.Api.CreateOrganizationAsync("Sjc before forgery");
            await gru.UntilOrganizationAsync(sjcBefore);

            // A forged push claiming sjc's node ID, with a credential that is not the shared one, reaches gru's internal listener.
            using (var forged = RawChannel(gru.Api.Server.BaseAddress, [PeerTls.NewSecret()]))
            {
                var client = new ReplicationRpc.ReplicationClient(forged);
                var status = await StatusOf(async () =>
                {
                    using var call = client.Push(cancellationToken: Token);
                    await WritePushFrameAsync(call, new PushFrame { Hello = new Hello { Node = sjc.Node.Value, Region = "sjc" } });
                    _ = await call.ResponseStream.MoveNext(Token);
                });
                await Assert.That(status).IsEqualTo(StatusCode.Unauthenticated);
            }

            // The legitimate streams carried on: each region's catalog write still reaches the other, both ways.
            var after = await gru.Api.CreateOrganizationAsync("After forgery");
            await sjc.UntilOrganizationAsync(after);
            var sjcAfter = await sjc.Api.CreateOrganizationAsync("Sjc after forgery");
            await gru.UntilOrganizationAsync(sjcAfter);
            await Assert.That(await gru.PeerStatusAsync("sjc")).IsEqualTo("ok");
            await Assert.That(await sjc.PeerStatusAsync("gru")).IsEqualTo("ok");
        }
        finally
        {
            await sjc.DisposeAsync();
            await gru.DisposeAsync();
        }
    }

    [Test]
    public async Task Mismatched_secrets_refuse_each_other_and_a_forged_catalog_write_changes_nothing()
    {
        // Two servers whose secrets differ: each one's credential is the other's refusal.
        var (mine, theirs) = (PeerTls.NewSecret(), PeerTls.NewSecret());
        await using var a = await TerraBaseTestServer.StartAsync(services: services => services.Replace(ServiceDescriptor.Singleton(new PeerCredential(mine))));
        await using var b = await TerraBaseTestServer.StartAsync(services: services => services.Replace(ServiceDescriptor.Singleton(new PeerCredential(theirs))));

        // Streams from each side, sent with its own secret, are refused by the other: the refusal is a direct RPC answer, so nothing waits.
        using (var toB = RawChannel(b.BaseAddress, [mine]))
        using (var toA = RawChannel(a.BaseAddress, [theirs]))
        {
            await Assert.That(await StatusOf(async () => await PushHelloAndBatchAsync(new ReplicationRpc.ReplicationClient(toB), InvalidDatabase())))
                .IsEqualTo(StatusCode.Unauthenticated).Because("a's secret on b");
            await Assert.That(await StatusOf(async () => await PushHelloAndBatchAsync(new ReplicationRpc.ReplicationClient(toA), InvalidDatabase())))
                .IsEqualTo(StatusCode.Unauthenticated).Because("b's secret on a");
        }

        // Positive control: b's own secret reaches validation, so the refusals above are the credential's.
        using (var own = PeerChannel.Create(b.BaseAddress, new PeerCredential(theirs)))
        {
            await Assert.That(await StatusOf(async () => await PushHelloAndBatchAsync(new ReplicationRpc.ReplicationClient(own), InvalidDatabase())))
                .IsEqualTo(StatusCode.InvalidArgument);
        }

        // A forged catalog write to b, claiming an operator, is refused and leaves b's catalog and keys as they were.
        var before = await CountsAsync(b);
        using var forged = RawHttp(b.BaseAddress, [mine]);
        using var commit = CommitRequest(EntityIds.New().ToString(), EntityIds.New().ToString());
        using var refused = await forged.SendAsync(commit, Token);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await CountsAsync(b)).IsEqualTo(before);
    }

    [Test]
    public async Task Local_production_entry_point_refuses_internal_requests_without_the_credential_and_serves_them_with_it()
    {
        var secret = PeerTls.NewSecret();
        var directory = Directory.CreateTempSubdirectory("terrabase-peer-process-").FullName;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(Deadline);
            using var process = StartBinary(directory, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PeerCredential.EnvironmentVariable] = secret,
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DOTNET_ENVIRONMENT"] = "Development",
            });
            try
            {
                var lines = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
                process.EnableRaisingEvents = true;
                process.OutputDataReceived += (_, line) => _ = line.Data is { } text && lines.Writer.TryWrite(text);
                process.ErrorDataReceived += (_, line) => _ = line.Data is { } text && lines.Writer.TryWrite(text);
                process.Exited += (_, _) => lines.Writer.TryComplete();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Kestrel binds the internal listener first, so its record is the first listening address (port 0, no fixed port).
                var internalAddress = await FirstListeningAddressAsync(lines.Reader, deadline.Token);
                using var anonymous = RawHttp(internalAddress, []);
                using var refused = await anonymous.GetAsync(new Uri("/healthz", UriKind.Relative), deadline.Token);
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized).Because("the process refuses a request without the credential");

                using var peer = RawHttp(internalAddress, [secret]);
                using var answered = await peer.GetAsync(new Uri("/healthz", UriKind.Relative), deadline.Token);
                await Assert.That(answered.StatusCode).IsEqualTo(HttpStatusCode.OK).Because("the process serves a request with the credential");
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
    // setting is passed empty; inherited ts-orch, TerraBase and AWS variables are removed so only the given environment applies.
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
            if (name == "master-key")
            {
                continue;
            }

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

        TestMasterKey.ConfigureProcess(start);

        return Process.Start(start) ?? throw new InvalidOperationException("the TerraBase process did not start");
    }

    private static async Task<Uri> FirstListeningAddressAsync(ChannelReader<string> lines, CancellationToken cancellationToken)
    {
        await foreach (var line in lines.ReadAllAsync(cancellationToken))
        {
            if (ListeningAddress(line) is { } address)
            {
                return address;
            }
        }

        throw new InvalidOperationException("the process exited before it listened");
    }

    // A JSON log record's address when it announces a listener, after an optional journald prefix; partial lines answer null.
    private static Uri? ListeningAddress(string line)
    {
        var json = line.IndexOf('{', StringComparison.Ordinal);
        if (json < 0 || !line.Contains("Now listening on", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(line[json..])?["address"]?.GetValue<string>() is { } address && Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A key-issue body for one organization: the admin route's valid shape, so only the credential can refuse it.
    private static StringContent KeyBody(string organization) =>
        new(JsonSerializer.Serialize(new { name = "forged", scopes = ReadScopes, organizationId = organization }), Encoding.UTF8, "application/json");

    // A management commit that creates one organization as the given user; the internal listener serves it.
    private static HttpRequestMessage CommitRequest(string owner, string organization)
    {
        var commit = JsonSerializer.Serialize(new
        {
            id = EntityIds.New().ToString(),
            ops = new object[] { new { op = "create_organization", id = organization, name = $"forged-{organization}" } },
        });
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/management/commits", UriKind.Relative))
        {
            Content = new StringContent(commit, Encoding.UTF8, "application/json"),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Add("X-TerraBase-User-Id", owner);
        return request;
    }

    // The documents a refused write would have created: API keys and organizations, counted in the system database.
    private static async Task<(long Keys, long Organizations)> CountsAsync(TerraBaseTestServer server)
    {
        var store = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(Token);
        await using var transaction = await store.BeginTransactionAsync(write: false, Token);
        return (await transaction.CountDocumentsAsync("api_keys", Token), await transaction.CountDocumentsAsync("organizations", Token));
    }

    // Raw HTTP/2 prior-knowledge channel with exactly the given credential headers and no default credential.
    private static GrpcChannel RawChannel(Uri address, string[] credentials) =>
        GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = new CredentialHeaders(credentials) { InnerHandler = PlainHandler() },
            DisposeHttpClient = true,
        });

    private static HttpClient RawHttp(Uri address, string[] credentials) =>
        new(new CredentialHeaders(credentials) { InnerHandler = PlainHandler() })
        {
            BaseAddress = address,
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private static SocketsHttpHandler PlainHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

    private static async Task PushHelloAndBatchAsync(ReplicationRpc.ReplicationClient client, WireDatabase invalid)
    {
        using var call = client.Push(cancellationToken: Token);
        await WritePushFrameAsync(call, new PushFrame { Hello = new Hello { Node = ReplicaId.Of(1, 7, 0).Value, Region = "sjc" } });
        _ = await call.ResponseStream.MoveNext(Token);
        await WritePushFrameAsync(call, new PushFrame { Batch = new Batch { Database = invalid, LastChangeId = 1, Groups = ByteString.Empty } });
        _ = await call.ResponseStream.MoveNext(Token);
    }

    private static async Task WritePushFrameAsync(AsyncDuplexStreamingCall<PushFrame, AckFrame> call, PushFrame frame)
    {
        try
        {
            await call.RequestStream.WriteAsync(frame);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && !Token.IsCancellationRequested)
        {
            // An early server refusal can cancel the writer before its response status is observed.
            _ = await call.ResponseStream.MoveNext(Token);
            throw;
        }
    }

    private static async Task ReadBucketFirstPageAsync(ReplicationRpc.ReplicationClient client, WireDatabase invalid)
    {
        using var call = client.ReadBucket(new BucketRequest { Database = invalid, Buckets = { 9999u } }, cancellationToken: Token);
        _ = await call.ResponseStream.MoveNext(Token);
    }

    private static async Task ReadSnapshotFirstPageAsync(ReplicationRpc.ReplicationClient client, WireDatabase invalid)
    {
        using var call = client.ReadSnapshot(new DigestRequest { Database = invalid }, cancellationToken: Token);
        _ = await call.ResponseStream.MoveNext(Token);
    }

    private static async Task<StatusCode> StatusOf(Func<Task> call)
    {
        try
        {
            await call();
            return StatusCode.OK;
        }
        catch (RpcException error)
        {
            return error.StatusCode;
        }
    }

    // Replaces the peer credential header on every request: none, one wrong value, or several.
    private sealed class CredentialHeaders(string[] values) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.Remove(PeerCredential.HeaderName);
            foreach (var value in values)
            {
                request.Headers.Add(PeerCredential.HeaderName, value);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class CredentialHeaderCount : IStartupFilter
    {
        public TaskCompletionSource<int> Count { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                if (context.Request.Path == "/peer-auth-duplicate")
                {
                    _ = Count.TrySetResult(context.Request.Headers[PeerCredential.HeaderName].Count);
                }

                return continuation(context);
            });
            next(app);
        };
    }

    // One machine of the two-node cluster, listed in the shared fake test directory while it runs.
    private sealed class Region(string name, FakeMemberStore members) : IAsyncDisposable
    {
        private readonly string _machine = EntityIds.New().ToString().ToLowerInvariant();
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-peer-auth-").FullName;
        private ManagementApi? _api;

        public string Name { get; } = name;

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{Name} is stopped");

        public ReplicaId Node => Api.Server.Services.GetRequiredService<LocalReplica>().Id;

        public async Task StartAsync() =>
            _api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = Name;
                settings["node-name"] = _machine;
                settings["data-directory"] = _directory;
            }, services => services.AddSingleton<IMemberDirectory>(members));

        public async Task UntilOrganizationAsync(string organization)
        {
            await TerraBaseTestServer.EventuallyAsync(async token => await OrganizationAsync(organization, token) is not null, Deadline);
        }

        // The catalog document (null when this region holds none), read from its system database.
        private async Task<string?> OrganizationAsync(string organization, CancellationToken token)
        {
            var store = await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
            await using var transaction = await store.BeginTransactionAsync(write: false, token);
            return await transaction.GetAsync("organizations", organization, token) is { } document
                ? Encoding.UTF8.GetString(document.Json.Span)
                : null;
        }

        // The state of the stream to peer named <paramref name="peer"/>, as this region's replication status reports it.
        public async Task<string?> PeerStatusAsync(string peer)
        {
            using var response = await Api.Server.Client.GetAsync(new Uri("/health/replication", UriKind.Relative), Token);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            foreach (var entry in body.RootElement.GetProperty("peers").EnumerateArray())
            {
                if (entry.GetProperty("region").GetString() == peer)
                {
                    return entry.GetProperty("status").GetString();
                }
            }

            return null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_api is not null)
            {
                await _api.DisposeAsync();
                _api = null;
            }

            Directory.Delete(_directory, recursive: true);
        }
    }
}
