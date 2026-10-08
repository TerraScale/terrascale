using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ExplainSqlBoundaryTests
{
    private const int MaxBodyBytes = 128 * 1024;
    private const long Budget = 4 * 1024 * 1024;
    private const string Sql = "SELECT * FROM people LIMIT 1";
    private const string Json = """{"sql":"SELECT * FROM people LIMIT 1"}""";
    private static readonly string[] EmailFields = ["email"];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_body_at_the_limit_accepts_a_fully_escaped_maximum_length_statement(bool chunked)
    {
        await using var api = await StartAsync();
        var database = await CreateDatabaseAsync(api);
        using var client = ProjectClient(api, database.Project);
        var reserved = ReservedBytes(api);
        var sql = Sql.PadRight(16 * 1024);
        var escaped = "{\"sql\":\"" + string.Concat(sql.Select(character => $"\\u{(int)character:x4}")) + "\"}";
        var body = escaped.PadRight(MaxBodyBytes);
        using var content = chunked
            ? (HttpContent)new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)))
            : new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(database)) { Content = content };
        request.Headers.TransferEncodingChunked = chunked;
        using var response = await client.SendAsync(request, TUnit.Core.TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean()).IsTrue();
        await AssertMemoryReleasedAsync(api, reserved);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Oversized_declared_and_chunked_bodies_are_rejected_and_release_memory(bool chunked)
    {
        await using var api = await StartAsync();
        var database = await CreateDatabaseAsync(api);
        using var client = ProjectClient(api, database.Project);
        var reserved = ReservedBytes(api);
        var body = new string(' ', MaxBodyBytes + 1 - Json.Length) + Json;
        using var content = chunked
            ? (HttpContent)new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)))
            : new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(database)) { Content = content };
        request.Headers.TransferEncodingChunked = chunked;
        request.Headers.ExpectContinue = !chunked;
        using var response = await client.SendAsync(request, TUnit.Core.TestContext.Current!.Execution.CancellationToken);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("payload_too_large");
        await AssertMemoryReleasedAsync(api, reserved);
        await AssertRecoveryAsync(client, database);
    }

    [Test]
    [Arguments("{oops", "application/json", false, HttpStatusCode.BadRequest)]
    [Arguments(Json, "text/plain", false, HttpStatusCode.UnsupportedMediaType)]
    [Arguments(Json, "application/json", true, HttpStatusCode.UnsupportedMediaType)]
    public async Task Invalid_JSON_and_unsupported_media_release_memory_and_allow_recovery(string body, string mediaType, bool encoded, HttpStatusCode expected)
    {
        await using var api = await StartAsync();
        var database = await CreateDatabaseAsync(api);
        using var client = ProjectClient(api, database.Project);
        var reserved = ReservedBytes(api);
        using var content = new StringContent(body, Encoding.UTF8, mediaType);
        if (encoded)
        {
            content.Headers.ContentEncoding.Add("gzip");
        }

        using var response = await client.PostAsync(Path(database), content, TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(expected);
        await AssertMemoryReleasedAsync(api, reserved);
        await AssertRecoveryAsync(client, database);
    }

    [Test]
    [Arguments(false, HttpStatusCode.NotFound)]
    [Arguments(true, HttpStatusCode.Forbidden)]
    public async Task Insufficient_project_access_is_rejected_without_receiving_the_body(bool wrongScope, HttpStatusCode expected)
    {
        await using var api = await StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, wrongScope ? database.Project : EntityIds.New().ToString(),
            wrongScope ? "documents:read" : "databases:read");
        var reserved = ReservedBytes(api);
        var headers = await SendHeadersOnlyAsync(api, client, database);
        await Assert.That(headers[0]).StartsWith($"HTTP/1.1 {(int)expected} ");
        await AssertMemoryReleasedAsync(api, reserved);
    }

    [Test]
    public async Task Memory_exhaustion_returns_503_before_body_read_and_recovers_after_release()
    {
        await using var api = await StartAsync();
        var database = await CreateDatabaseAsync(api);
        using var client = ProjectClient(api, database.Project);
        var budget = api.Server.Services.GetRequiredService<IMemoryBudget>();
        var reserved = ReservedBytes(api);
        using (var held = budget.TryReserve(MemoryLane.Foreground, Budget - reserved - 1024 * 1024 + 1))
        {
            await Assert.That(held).IsNotNull();
            var headers = await SendHeadersOnlyAsync(api, client, database);
            await Assert.That(headers[0]).StartsWith("HTTP/1.1 503 ");
        }

        await AssertMemoryReleasedAsync(api, reserved);
        await AssertRecoveryAsync(client, database);
        await AssertMemoryReleasedAsync(api, reserved);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task A_stalled_HTTP2_upload_or_catalog_lookup_times_out_and_releases_memory_before_the_client_finishes(int catalogLookup)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var time = new FakeTimeProvider();
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(
            settings => settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture), hook.Install, time: time);
        var database = await CreateDatabaseAsync(api);
        using var client = ProjectClient(api, database.Project);
        var reserved = ReservedBytes(api);
        var catalogEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogLookups = 0;
        if (catalogLookup > 0)
        {
            hook.AfterGet = async (collection, id, cancellationToken) =>
            {
                if (collection == $"projects/{database.Project}/database_names" && id == database.Name
                    && Interlocked.Increment(ref catalogLookups) == catalogLookup)
                {
                    hook.AfterGet = null;
                    catalogEntered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
            };
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var upload = new Pipe(new PipeOptions(pauseWriterThreshold: 64, resumeWriterThreshold: 32));
        using var content = new StreamContent(upload.Reader.AsStream());
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(database))
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        try
        {
            await upload.Writer.WriteAsync("{"u8.ToArray(), deadline.Token);
            var pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (catalogLookup > 0)
            {
                await catalogEntered.Task.WaitAsync(deadline.Token);
                if (catalogLookup == 2)
                {
                    await Assert.That(ReservedBytes(api)).IsGreaterThanOrEqualTo(reserved + MaxBodyBytes * 8);
                }
                else
                {
                    await Assert.That(ReservedBytes(api)).IsLessThan(reserved + MaxBodyBytes * 8);
                }
            }
            else
            {
                while (ReservedBytes(api) != reserved + MaxBodyBytes * 8)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
            }

            time.Advance(TimeSpan.FromSeconds(31));
            using var response = await pending.WaitAsync(deadline.Token);
            await Assert.That(response.Version).IsEqualTo(HttpVersion.Version20);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestTimeout);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("request_timeout");
            await AssertMemoryReleasedAsync(api, reserved);
            await AssertRecoveryAsync(client, database);
            await AssertMemoryReleasedAsync(api, reserved);
        }
        finally
        {
            // The upload stays unfinished through the response, memory checks, and recovery request.
            await upload.Writer.CompleteAsync();
            await deadline.CancelAsync();
        }
    }

    [Test]
    public async Task A_slow_HTTP1_upload_returns_request_timeout_when_Kestrel_enforces_its_data_rate()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var time = new FakeTimeProvider();
        var started = time.GetUtcNow();
        await using var api = await ManagementApi.StartAsync(
            settings => settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture),
            services => services.Configure<KestrelServerOptions>(options =>
                options.Limits.MinRequestBodyDataRate = new MinDataRate(1024, TimeSpan.FromSeconds(2))), time);
        var database = await CreateDatabaseAsync(api);
        using var client = ProjectClient(api, database.Project);
        var reserved = ReservedBytes(api);
        var address = api.Server.ClientAddress!;
        using var socket = new TcpClient();
        await socket.ConnectAsync(address.Host, address.Port, deadline.Token);
        using var handler = api.Tls.CreateHandler();
        await using var tls = new SslStream(socket.GetStream());
        handler.SslOptions.TargetHost = address.Host;
        await tls.AuthenticateAsClientAsync(handler.SslOptions, deadline.Token);
        var headers = $"POST {Path(database)} HTTP/1.1\r\nHost: {address.Authority}\r\nAuthorization: {client.DefaultRequestHeaders.Authorization}\r\nContent-Type: application/json\r\nContent-Length: 64\r\nConnection: close\r\n\r\n{{";
        await tls.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token);
        var buffer = new byte[4096];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await tls.ReadAsync(buffer.AsMemory(length), deadline.Token)) > 0)
        {
            length += read;
        }

        await Assert.That(length).IsLessThan(buffer.Length);
        var response = Encoding.UTF8.GetString(buffer, 0, length);
        await Assert.That(response).StartsWith("HTTP/1.1 408 ");
        await Assert.That(response).Contains("Content-Type: application/json");
        await Assert.That(response).Contains("\"code\":\"request_timeout\"");
        // The endpoint clock never advanced: this response came from Kestrel's transport deadline.
        await Assert.That(time.GetUtcNow()).IsEqualTo(started);
        await AssertMemoryReleasedAsync(api, reserved);
        await AssertRecoveryAsync(client, database);
        await AssertMemoryReleasedAsync(api, reserved);
    }

    [Test]
    public async Task A_catalog_follower_explains_a_registered_database_before_its_documents_arrive()
    {
        using var peers = new PeerTls();
        await using var primary = await ManagementApi.StartAsync(peers.Apply);
        var database = await primary.CreateManagedDatabaseAsync();
        await primary.VerifyProjectBindingAsync(database.Organization, database.Project);
        await using var follower = await ManagementApi.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["catalog-primary"] = primary.Server.PeerAddress!.ToString();
        });
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var ready = await follower.Server.Client.GetAsync(new Uri("/readyz", UriKind.Relative), token);
            return ready.StatusCode == HttpStatusCode.OK;
        });
        var file = System.IO.Path.Combine(follower.Server.DataDirectory, database.Organization, database.Project, database.Id + ".db");
        await Assert.That(File.Exists(file)).IsFalse();
        using var client = ProjectClient(follower, database.Project);
        await AssertRecoveryAsync(client, database);
        await Assert.That(File.Exists(file)).IsTrue();
    }

    [Test]
    public async Task A_catalog_follower_does_not_create_database_storage_for_rejected_requests()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var peers = new PeerTls();
        await using var primary = await ManagementApi.StartAsync(peers.Apply);
        var database = await primary.CreateManagedDatabaseAsync();
        await primary.VerifyProjectBindingAsync(database.Organization, database.Project);
        await using var follower = await ManagementApi.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["catalog-primary"] = primary.Server.PeerAddress!.ToString();
            settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture);
        });
        await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
        {
            using var ready = await follower.Server.Client.GetAsync(new Uri("/readyz", UriKind.Relative), cancellationToken);
            return ready.StatusCode == HttpStatusCode.OK;
        });
        var file = System.IO.Path.Combine(follower.Server.DataDirectory, database.Organization, database.Project, database.Id + ".db");
        await Assert.That(File.Exists(file)).IsFalse();
        using var client = ProjectClient(follower, database.Project);
        var reserved = ReservedBytes(follower);
        foreach (var (mediaType, encoded, body, expected) in new[]
        {
            ("text/plain", false, Json, HttpStatusCode.UnsupportedMediaType),
            ("application/json", true, Json, HttpStatusCode.UnsupportedMediaType),
            ("application/json", false, Json.PadRight(MaxBodyBytes + 1), HttpStatusCode.RequestEntityTooLarge),
            ("application/json", false, "{oops", HttpStatusCode.BadRequest),
            ("application/json", false, """{"sql":"DELETE FROM people"}""", HttpStatusCode.BadRequest),
        })
        {
            using var content = new StringContent(body, Encoding.UTF8, mediaType);
            if (encoded)
            {
                content.Headers.ContentEncoding.Add("gzip");
            }

            using var response = await client.PostAsync(Path(database), content, token);
            await Assert.That(response.StatusCode).IsEqualTo(expected);
            await Assert.That(File.Exists(file)).IsFalse();
            await AssertMemoryReleasedAsync(follower, reserved);
        }

        using (var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(Json.PadRight(MaxBodyBytes + 1)))))
        using (var request = new HttpRequestMessage(HttpMethod.Post, Path(database)) { Content = content })
        {
            content.Headers.ContentType = new("application/json");
            request.Headers.TransferEncodingChunked = true;
            using var response = await client.SendAsync(request, token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
            await Assert.That(File.Exists(file)).IsFalse();
            await AssertMemoryReleasedAsync(follower, reserved);
        }

        var budget = follower.Server.Services.GetRequiredService<IMemoryBudget>();
        using (var held = budget.TryReserve(MemoryLane.Foreground, Budget - reserved - 1024 * 1024 + 1))
        {
            await Assert.That(held).IsNotNull();
            var headers = await SendHeadersOnlyAsync(follower, client, database);
            await Assert.That(headers[0]).StartsWith("HTTP/1.1 503 ");
            await Assert.That(File.Exists(file)).IsFalse();
        }

        await AssertMemoryReleasedAsync(follower, reserved);
        await AssertRecoveryAsync(client, database);
        await Assert.That(File.Exists(file)).IsTrue();
        await AssertMemoryReleasedAsync(follower, reserved);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Placement_is_checked_before_the_body_even_with_a_stale_file_or_only_a_catalog_record(bool catalogOnly)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-explain-wan-").FullName;
        try
        {
            await using var api = await StartWanAsync(directory);
            var local = await CreateDatabaseAsync(api);
            // Seed real catalog state from another home, without replacing tenant or query services.
            // The existing local file represents a retained replica after its region leaves placement.
            var database = catalogOnly ? local with { Id = EntityIds.New().ToString(), Name = "remote" } : local;
            await SeedRemotePlacementAsync(api, database, TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            var file = System.IO.Path.Combine(api.Server.DataDirectory, database.Organization, database.Project, database.Id + ".db");
            await Assert.That(File.Exists(file)).IsEqualTo(!catalogOnly);
            using var client = ProjectClient(api, database.Project);
            var headers = await SendHeadersOnlyAsync(api, client, database);
            await Assert.That(headers[0]).StartsWith("HTTP/1.1 421 ");
            await Assert.That(headers).Contains("X-TerraBase-Database-Home: sfo-1");
            await Assert.That(headers).Contains("X-TerraBase-Database-Regions: sfo-1");
            using var response = await client.PostAsJsonAsync(Path(database), new { sql = Sql });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("wrong_region");
            await Assert.That(File.Exists(file)).IsEqualTo(!catalogOnly);

            foreach (var restricted in new[] { false, true })
            {
                using var outsider = ProjectClient(api, restricted ? database.Project : EntityIds.New().ToString(),
                    database: restricted ? "other" : null);
                var hidden = await SendHeadersOnlyAsync(api, outsider, database);
                await Assert.That(hidden[0]).StartsWith("HTTP/1.1 404 ");
                await Assert.That(hidden.Any(header => header.StartsWith("X-TerraBase-Database-", StringComparison.OrdinalIgnoreCase))).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_reassigned_database_name_is_rejected_before_body_read_or_planning(bool headersOnly)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-explain-race-").FullName;
        try
        {
            var hook = new UsageCommitHook();
            await using var api = await StartWanAsync(directory, hook);
            var database = await CreateDatabaseAsync(api);
            var replacement = database with { Id = EntityIds.New().ToString() };
            var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
            var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
            await Assert.That(await engine.CreateDatabaseAsync(
                new TenantContext(database.Organization, PlanTier.Hobby, database.Project, replacement.Id), token)).IsTrue();
            using var client = ProjectClient(api, database.Project);
            var opened = 0;
            hook.BeforeBegin = (_, _) =>
            {
                Interlocked.Increment(ref opened);
                return ValueTask.CompletedTask;
            };
            hook.AfterGet = async (collection, id, _) =>
            {
                if (collection == $"projects/{database.Project}/database_names" && id == database.Name)
                {
                    // The first resolver already captured the local id. Its placement remains local,
                    // but the second resolver now sees a different id with a retained remote replica.
                    hook.AfterGet = null;
                    await SeedRemotePlacementAsync(api, replacement, token);
                }
            };

            if (headersOnly)
            {
                var headers = await SendHeadersOnlyAsync(api, client, database);
                await Assert.That(headers[0]).StartsWith("HTTP/1.1 404 ");
            }
            else
            {
                using var response = await client.PostAsJsonAsync(Path(database), new { sql = Sql }, token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            await Assert.That(hook.AfterGet is null).IsTrue();
            await Assert.That(Volatile.Read(ref opened)).IsEqualTo(0);
            using var subsequent = await client.PostAsJsonAsync(Path(database), new { sql = Sql }, token);
            await Assert.That(subsequent.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            await Assert.That(Volatile.Read(ref opened)).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_missing_database_record_is_rejected_even_when_its_name_and_file_remain()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-explain-missing-record-").FullName;
        try
        {
            var hook = new UsageCommitHook();
            await using var api = await StartWanAsync(directory, hook);
            var database = await CreateDatabaseAsync(api);
            var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
            var system = await engine.OpenSystemDatabaseAsync(token);
            await using (var transaction = await system.BeginTransactionAsync(write: true, token))
            {
                await Assert.That(await transaction.DeleteAsync($"projects/{database.Project}/databases", database.Id, token)).IsTrue();
                await transaction.CommitAsync(token);
            }

            var file = System.IO.Path.Combine(api.Server.DataDirectory, database.Organization, database.Project, database.Id + ".db");
            await Assert.That(File.Exists(file)).IsTrue();
            var opened = 0;
            hook.BeforeBegin = (_, _) =>
            {
                Interlocked.Increment(ref opened);
                return ValueTask.CompletedTask;
            };
            using var client = ProjectClient(api, database.Project);
            var headers = await SendHeadersOnlyAsync(api, client, database);
            await Assert.That(headers[0]).StartsWith("HTTP/1.1 404 ");
            using var response = await client.PostAsJsonAsync(Path(database), new { sql = Sql }, token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(Volatile.Read(ref opened)).IsEqualTo(0);
            await Assert.That(File.Exists(file)).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task A_placement_change_for_the_same_database_is_rechecked_before_planning(int capturedRead)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-explain-placement-race-").FullName;
        try
        {
            var hook = new UsageCommitHook();
            await using var api = await StartWanAsync(directory, hook);
            var database = await CreateDatabaseAsync(api);
            using var client = ProjectClient(api, database.Project);
            var opened = 0;
            var reads = 0;
            hook.BeforeBegin = (_, _) =>
            {
                Interlocked.Increment(ref opened);
                return ValueTask.CompletedTask;
            };
            hook.AfterGet = async (collection, id, _) =>
            {
                if (collection == $"projects/{database.Project}/databases" && id == database.Id
                    && Interlocked.Increment(ref reads) == capturedRead)
                {
                    // Keep the name and identity intact; only the captured placement becomes stale.
                    hook.AfterGet = null;
                    await SeedRemotePlacementAsync(api, database, token);
                }
            };

            if (capturedRead == 1)
            {
                var headers = await SendHeadersOnlyAsync(api, client, database);
                await Assert.That(headers[0]).StartsWith("HTTP/1.1 421 ");
            }
            else
            {
                using var response = await client.PostAsJsonAsync(Path(database), new { sql = Sql }, token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            }

            await Assert.That(hook.AfterGet is null).IsTrue();
            await Assert.That(Volatile.Read(ref opened)).IsEqualTo(0);
            using var subsequent = await client.PostAsJsonAsync(Path(database), new { sql = Sql }, token);
            await Assert.That(subsequent.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            await Assert.That(Volatile.Read(ref opened)).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_name_or_placement_change_during_transaction_acquisition_does_not_disclose_the_retained_plan(bool reassignName)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-explain-transaction-race-").FullName;
        try
        {
            var hook = new UsageCommitHook();
            await using var api = await StartWanAsync(directory, hook);
            var database = await CreateDatabaseAsync(api);
            using var writer = await api.CreateClientAsync(database.Organization, "documents:write");
            using var written = await writer.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "private")),
                new { email = new Faker().Internet.Email() }, token);
            await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var created = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/indexes"),
                new { collection = "people", fields = EmailFields }, token);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var client = ProjectClient(api, database.Project);
            const string IndexedSql = "SELECT email FROM people ORDER BY email LIMIT 1";
            using (var before = await client.PostAsJsonAsync(Path(database), new { sql = IndexedSql }, token))
            {
                await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var plan = await before.Content.ReadFromJsonAsync<JsonElement>(token);
                await Assert.That(plan.GetProperty("indexes")[0].GetString()).IsEqualTo("people_email");
            }

            var replacement = reassignName ? database with { Id = EntityIds.New().ToString() } : database;
            var reserved = ReservedBytes(api);
            hook.BeforeBegin = async (tenant, cancellationToken) =>
            {
                if (tenant.DatabaseId == database.Id)
                {
                    hook.BeforeBegin = null;
                    await SeedRemotePlacementAsync(api, replacement, cancellationToken);
                }
            };

            using var response = await client.PostAsJsonAsync(Path(database), new { sql = IndexedSql }, token);
            await Assert.That(hook.BeforeBegin is null).IsTrue();
            await Assert.That(response.StatusCode).IsEqualTo(reassignName ? HttpStatusCode.NotFound : HttpStatusCode.MisdirectedRequest);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo(reassignName ? "not_found" : "wrong_region");
            await Assert.That(body.TryGetProperty("plan", out _)).IsFalse();
            await Assert.That(body.TryGetProperty("indexes", out _)).IsFalse();
            await Assert.That(body.GetRawText()).DoesNotContain("people_email");
            await Assert.That(response.Headers.Contains("X-TerraBase-Database-Home")).IsEqualTo(!reassignName);
            await AssertMemoryReleasedAsync(api, reserved);
            await Assert.That(File.Exists(System.IO.Path.Combine(api.Server.DataDirectory,
                database.Organization, database.Project, database.Id + ".db"))).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<ManagementApi> StartWanAsync(string directory, UsageCommitHook? hook = null)
    {
        var credentials = System.IO.Path.Combine(directory, "user.creds");
        await File.WriteAllTextAsync(credentials, "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n");
        var network = new FakeRegionNetwork();
        return await ManagementApi.StartAsync(settings =>
        {
            settings["region"] = "gru-1";
            settings["node-name"] = "gru-member";
            settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
            settings["nats-url"] = "nats://127.0.0.1:4222";
            settings["nats-credentials-file"] = credentials;
        }, services =>
        {
            services.AddSingleton(network.Join("gru"));
            services.AddSingleton<IMemberStore>(new FakeMemberStore(TimeProvider.System));
            hook?.Install(services);
        });
    }

    private static Task<ManagementApi> StartAsync() =>
        ManagementApi.StartAsync(settings => settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture));

    private static async Task<ManagedDatabaseRef> CreateDatabaseAsync(ManagementApi api)
    {
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        return database;
    }

    private static Uri Path(ManagedDatabaseRef database) =>
        ManagementApi.Uri($"/projects/{database.Project}/databases/{database.Name}/sql/explain");

    private static HttpClient ProjectClient(ManagementApi api, string project, string scope = "databases:read", string? database = null) =>
        api.WithToken(api.Issuer.Mint(api.User, descriptor =>
        {
            descriptor.Claims["project_id"] = project;
            descriptor.Claims["scope"] = scope;
            if (database is not null)
            {
                descriptor.Claims["database"] = database;
            }
        }));

    private static async Task AssertRecoveryAsync(HttpClient client, ManagedDatabaseRef database)
    {
        using var response = await client.PostAsJsonAsync(Path(database), new { sql = Sql });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean()).IsTrue();
    }

    private static async Task AssertMemoryReleasedAsync(ManagementApi api, long baseline) =>
        await Assert.That(ReservedBytes(api)).IsEqualTo(baseline);

    private static long ReservedBytes(ManagementApi api)
    {
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long? reserved = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            // Observe only this test's server while the suite runs in parallel.
            if (instrument.Name == "terrabase.memory.reserved" && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => reserved = value);
        listener.Start();
        listener.RecordObservableInstruments();
        return reserved ?? throw new InvalidOperationException("the server's memory budget gauge was not observed");
    }

    private static async Task SeedRemotePlacementAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken token)
    {
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var transaction = await system.BeginTransactionAsync(write: true, token);
        var record = new ManagedDatabase(database.Id, database.Project, database.Name, null, DateTimeOffset.UtcNow,
            HomeRegion: "sfo-1", ReplicaRegions: [], PlacementVersion: 2);
        await transaction.PutAsync($"projects/{database.Project}/databases", database.Id,
            JsonSerializer.SerializeToUtf8Bytes(record, TerraBaseJsonContext.Default.ManagedDatabase), token);
        await transaction.PutAsync($"projects/{database.Project}/database_names", database.Name,
            JsonSerializer.SerializeToUtf8Bytes(database.Id, TerraBaseJsonContext.Default.String), token);
        await transaction.CommitAsync(token);
    }

    // Send headers promising a body, but no body bytes: a response proves rejection precedes binding/read.
    private static async Task<List<string>> SendHeadersOnlyAsync(ManagementApi api, HttpClient client, ManagedDatabaseRef database)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var address = api.Server.ClientAddress!;
        using var socket = new TcpClient();
        await socket.ConnectAsync(address.Host, address.Port, deadline.Token);
        using var handler = api.Tls.CreateHandler();
        await using var tls = new SslStream(socket.GetStream());
        handler.SslOptions.TargetHost = address.Host;
        await tls.AuthenticateAsClientAsync(handler.SslOptions, deadline.Token);
        var headers = $"POST {Path(database)} HTTP/1.1\r\nHost: {address.Authority}\r\nAuthorization: {client.DefaultRequestHeaders.Authorization}\r\nContent-Type: application/json\r\nContent-Length: 1\r\nConnection: close\r\n\r\n";
        await tls.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token);
        using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
        var result = new List<string>();
        while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } line)
        {
            result.Add(line);
        }

        return result;
    }
}
