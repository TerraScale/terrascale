using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bogus;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>A catalog follower forwards what its catalog primary serves (docs/decisions/0009).</summary>
public sealed class CatalogForwardingTests
{
    private static readonly string[] Scopes = [ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite];

    [Test]
    public async Task Project_JWTs_provision_through_a_follower_and_serve_documents_on_both_members_while_legacy_aliases_keep_their_identity()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        var faker = new Faker();
        await using var primary = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            tls.Apply(settings);
            TestIssuer.Configure(settings);
        }, issuer.Route);
        await using var follower = await StartFollowerAsync(peers, tls, issuer, primary.PeerAddress!);
        await ReadyAsync(follower);

        var project = EntityIds.New().ToString();
        var actor = EntityIds.New().ToString();
        const string allScopes = "databases:read databases:write documents:read documents:write shapes:read";
        string Token(string target, string? scope)
        {
            return issuer.Mint(actor, token =>
            {
                token.Claims["project_id"] = target;
                if (scope is not null)
                {
                    token.Claims["scope"] = scope;
                }
            });
        }

        var databases = $"/v1/projects/{project}/databases";
        var database = databases + "/main";
        using var jwt = tls.CreateClient(follower.ClientAddress!, Token(project, allScopes));
        using (var created = await jwt.PostAsJsonAsync(Relative(databases), new { name = "main" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
            await Assert.That(created.Headers.Location?.OriginalString).IsEqualTo(database);
            await Assert.That((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()).IsEqualTo(project);
        }

        using var nonmember = tls.CreateClient(follower.ClientAddress!, issuer.Mint(actor));
        using (var denied = await nonmember.GetAsync(Relative($"/v1/management/organizations/{project}/projects/{project}")))
        {
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        // The primary must receive the original aliases, or it would require project claims from this dashboard token.
        var signIn = issuer.Mint(EntityIds.New().ToString());
        using var dashboard = tls.CreateClient(follower.ClientAddress!, signIn);
        var organization = await CreatedIdAsync(dashboard, "/v1/management/organizations", new { name = faker.Company.CompanyName() });
        var legacyProject = await CreatedIdAsync(dashboard, $"/v1/management/organizations/{organization}/projects", new { name = "Legacy" });
        var legacyDatabases = $"/v1/management/organizations/{organization}/projects/{legacyProject}/databases";
        var legacyDatabase = await CreatedIdAsync(dashboard, legacyDatabases, new { name = "legacy" });
        using (var read = await dashboard.GetAsync(Relative(legacyDatabases + "/" + legacyDatabase)))
        {
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()).IsEqualTo(legacyDatabase);
        }

        // A migration requested at a follower is verified once on the primary, then its binding is replicated before answering.
        var legacyData = $"/v1/projects/{legacyProject}/databases/legacy";
        using var legacyJwt = tls.CreateClient(follower.ClientAddress!, Token(legacyProject, allScopes));
        using (var denied = await legacyJwt.GetAsync(Relative(legacyData)))
        {
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        issuer.GrantProject(signIn, organization, legacyProject);
        using (var verified = await dashboard.PostAsJsonAsync(Relative(legacyDatabases[..^"/databases".Length] + "/binding"), new { organization }))
        {
            await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        await Assert.That(issuer.ProjectFetches).IsEqualTo(1);
        foreach (var address in new[] { follower.ClientAddress!, primary.ClientAddress! })
        {
            using var reader = tls.CreateClient(address, Token(legacyProject, allScopes));
            await Assert.That((await reader.GetFromJsonAsync<JsonElement>(Relative(legacyData))).GetProperty("id").GetString()).IsEqualTo(legacyDatabase);
        }

        // Catalog following does not replicate document files; exercise each member's real data listener independently.
        foreach (var address in new[] { follower.ClientAddress!, primary.ClientAddress! })
        {
            using var data = tls.CreateClient(address, Token(project, allScopes));
            await Assert.That((await data.GetFromJsonAsync<JsonElement>(Relative(database))).GetProperty("projectId").GetString()).IsEqualTo(project);
            var collection = database + "/collections/people";
            using (var created = await data.PostAsJsonAsync(Relative(database + "/collections"), new { name = "people" }))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            await Assert.That((await data.GetFromJsonAsync<JsonElement>(Relative(collection))).GetProperty("name").GetString()).IsEqualTo("people");
            var document = collection + "/documents/by-key?id=" + EntityIds.New();
            var name = faker.Name.FullName();
            using (var written = await data.PutAsJsonAsync(Relative(document), new { name, count = 1 }))
            {
                await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            await Assert.That((await data.GetFromJsonAsync<JsonElement>(Relative(document))).GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
            using var patch = JsonContent.Create(new { count = 2 }, mediaType: new MediaTypeHeaderValue("application/merge-patch+json"));
            using (var updated = await data.PatchAsync(Relative(document), patch))
            {
                await Assert.That(updated.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            using (var query = await data.PostAsJsonAsync(Relative(collection + "/query"), new { filter = new { eq = new object[] { "/count", 2 } } }))
            {
                await Assert.That(query.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That((await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength()).IsEqualTo(1);
            }

            using var noScope = tls.CreateClient(address, Token(project, scope: null));
            using (var denied = await noScope.GetAsync(Relative(document)))
            {
                await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
                await Assert.That(await CodeAsync(denied)).IsEqualTo("forbidden_scope");
            }

            using var readOnly = tls.CreateClient(address, Token(project, "databases:read documents:read"));
            using (var denied = await readOnly.PostAsJsonAsync(Relative(databases), new { name = "denied" }))
            {
                await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
                await Assert.That(await CodeAsync(denied)).IsEqualTo("forbidden_scope");
            }

            using (var denied = await readOnly.PutAsJsonAsync(Relative(document), new { count = 3 }))
            {
                await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
                await Assert.That(await CodeAsync(denied)).IsEqualTo("forbidden_scope");
            }

            using var wrongProject = tls.CreateClient(address, Token(EntityIds.New().ToString(), allScopes));
            foreach (var path in new[] { database, document })
            {
                using var denied = await wrongProject.GetAsync(Relative(path));
                await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                await Assert.That(await CodeAsync(denied)).IsEqualTo("unknown_database");
            }

            await Assert.That((await data.GetFromJsonAsync<JsonElement>(Relative(document))).GetProperty("document").GetProperty("count").GetInt32()).IsEqualTo(2);
            using (var deleted = await data.DeleteAsync(Relative(document)))
            {
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            using var missing = await data.GetAsync(Relative(document));
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task A_follower_with_a_frozen_earlier_clock_forwards_a_catalog_write_over_the_real_peer_listener()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        var frozen = DateTimeOffset.UtcNow.AddMinutes(-3);
        var time = new FakeTimeProvider(frozen);
        await using var primary = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            tls.Apply(settings);
            TestIssuer.Configure(settings);
        }, issuer.Route);
        await using var follower = await StartFollowerAsync(peers, tls, issuer, primary.PeerAddress!,
            services => services.AddSingleton<TimeProvider>(time));
        await ReadyAsync(follower);
        var token = issuer.Mint(EntityIds.New().ToString(), descriptor => descriptor.NotBefore = time.GetUtcNow().UtcDateTime);
        using var client = tls.CreateClient(follower.ClientAddress!, token);
        var name = new Faker().Company.CompanyName();
        var organization = await CreatedIdAsync(client, "/v1/management/organizations", new { name });
        foreach (var address in new[] { primary.ClientAddress!, follower.ClientAddress! })
        {
            using var reader = tls.CreateClient(address, token);
            var saved = await reader.GetFromJsonAsync<JsonElement>(Relative($"/v1/management/organizations/{organization}"));
            await Assert.That(saved.GetProperty("name").GetString()).IsEqualTo(name);
        }

        await Assert.That(time.GetUtcNow()).IsEqualTo(frozen);
    }

    [Test]
    public async Task Management_requests_through_a_follower_are_served_by_the_primary_as_the_follower_and_what_they_wrote_serves_there_at_once()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        var credentials = Directory.CreateTempSubdirectory("terrabase-service-").FullName;
        TerraBaseTestServer? primary = null;
        try
        {
            var credential = Path.Combine(credentials, "credential");
            await File.WriteAllTextAsync(credential, ManagementApi.ServiceCredential + "\n");
            void Configure(Dictionary<string, string> settings)
            {
                peers.Apply(settings);
                tls.Apply(settings);
                TestIssuer.Configure(settings);
                settings["management-credential-file"] = credential;
            }

            primary = await TerraBaseTestServer.StartAsync(settings =>
            {
                Configure(settings);
                // Catalog following is in-region; forwarded response metadata does not change database placement.
                settings["region"] = "follower-region";
            }, issuer.Route);
            await using var follower = await TerraBaseTestServer.StartAsync(settings =>
            {
                Configure(settings);
                settings["region"] = "follower-region";
                settings["catalog-primary"] = primary.PeerAddress!.ToString();
            }, issuer.Route);
            await ReadyAsync(follower);

            // ts-dashboard's calls through the follower, pinned to its region: an organization, a project, a database and a key.
            var user = EntityIds.New().ToString();
            using var dashboard = tls.CreateClient(follower.ClientAddress!, issuer.Mint(user));
            dashboard.DefaultRequestHeaders.Add("X-TerraScale-Region-Pin", "follower-region");
            var organization = await CreatedIdAsync(dashboard, "/v1/management/organizations", new { name = "Forwarded" });
            var project = await CreatedIdAsync(dashboard, $"/v1/management/organizations/{organization}/projects", new { name = "main" });
            using (var database = await dashboard.PostAsJsonAsync(Relative($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "reports" }))
            {
                // The follower answered it: its region, in the header and in the body.
                await Assert.That(database.StatusCode).IsEqualTo(HttpStatusCode.Created);
                await Assert.That(database.Headers.GetValues("X-TerraBase-Region").Single()).IsEqualTo("follower-region");
                await Assert.That((await database.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("region").GetString()).IsEqualTo("follower-region");
            }

            string key;
            using (var issued = await dashboard.PostAsJsonAsync(
                Relative($"/v1/management/organizations/{organization}/projects/{project}/api-keys"), new { name = "app", scopes = Scopes }))
            {
                await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.Created);
                key = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
            }

            // The key, and a database it creates through the follower, serve there as soon as the requests answered.
            using var data = tls.CreateClient(follower.ClientAddress!, key);
            await Assert.That(await PutDatabaseAsync(data, project, "orders")).IsEqualTo(HttpStatusCode.Created);
            using (var withBody = await data.PutAsJsonAsync(Relative($"/v1/projects/{project}/databases/archive"), new { }))
            {
                await Assert.That(withBody.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            using (var collection = await data.PostAsJsonAsync(Relative($"/v1/projects/{project}/databases/orders/collections"), new { name = "items" }))
            {
                await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            // The primary wrote all of it, and serves the follower's Management reads.
            await Assert.That((await primary.Services.GetRequiredService<IManagementService>().GetOrganizationAsync(user, organization, CancellationToken.None)).Value)
                .IsTypeOf<Organization>();
            using (var read = await dashboard.GetAsync(Relative($"/v1/management/organizations/{organization}")))
            {
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()).IsEqualTo("Forwarded");
            }

            // An existing database answers here, unforwarded; so do requests the follower refuses itself.
            await Assert.That(await PutDatabaseAsync(data, project, "orders")).IsEqualTo(HttpStatusCode.OK);
            foreach (var token in new string?[] { null, TestIssuer.MintUnsigned(user), ManagementApi.ServiceCredential })
            {
                using var client = tls.CreateClient(follower.ClientAddress!, token);
                using var refused = await client.GetAsync(Relative("/v1/management/organizations"));
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }

            await Assert.That(await follower.MetricAsync("terrabase_catalog_forwarded_total", "result=\"answered\"")).IsEqualTo(7);

            // The loopback listener is never forwarded: its catalog writes stay the primary's own, with nothing written here.
            using var service = new HttpClient { BaseAddress = follower.BaseAddress };
            service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ManagementApi.ServiceCredential);
            service.DefaultRequestHeaders.Add("X-TerraBase-User-Id", user);
            using (var member = await service.PutAsJsonAsync(Relative($"/v1/management/organizations/{organization}/members/{EntityIds.New()}"), new { email = "m@example.com" }))
            {
                await NotPrimaryAsync(member);
            }

            using (var operatorKey = await follower.Client.PostAsJsonAsync(
                Relative("/admin/api-keys"), new { name = "denied", scopes = Scopes, organizationId = organization }))
            {
                await NotPrimaryAsync(operatorKey);
            }

            // A follower serves no forwarded request.
            using (var channel = peers.CreateChannel(follower.PeerAddress!))
            {
                var notPrimary = await Assert.ThrowsAsync<RpcException>(async () =>
                    await ForwardAsync(new CatalogForwarding.CatalogForwardingClient(channel), Request("GET", "/v1/management/organizations")));
                await Assert.That(notPrimary!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
            }

            // Without its primary the follower still serves existing databases; what the primary serves is unavailable.
            await primary.DisposeAsync();
            primary = null;
            await Assert.That(await PutDatabaseAsync(data, project, "orders")).IsEqualTo(HttpStatusCode.OK);
            foreach (var request in new Func<Task<HttpResponseMessage>>[]
            {
                () => data.PutAsync(Relative($"/v1/projects/{project}/databases/new"), null),
                () => dashboard.GetAsync(Relative($"/v1/management/organizations/{organization}")),
            })
            {
                using var unavailable = await request();
                await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                await Assert.That(unavailable.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
                await Assert.That(await CodeAsync(unavailable)).IsEqualTo("catalog_primary_unavailable");
            }
        }
        finally
        {
            if (primary is not null)
            {
                await primary.DisposeAsync();
            }

            Directory.Delete(credentials, recursive: true);
        }
    }

    [Test]
    public async Task The_primary_serves_a_forwarded_request_only_as_its_client_listener_would_and_only_on_an_endpoint_it_serves()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        await using var primary = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            tls.Apply(settings);
            TestIssuer.Configure(settings);
        }, issuer.Route);
        var user = EntityIds.New().ToString();
        var management = primary.Services.GetRequiredService<IManagementService>();
        var organization = await management.CreateOrganizationAsync(user, "Forwarded", CancellationToken.None) switch
        {
            Organization value => value,
            _ => throw new InvalidOperationException("the catalog fixture was not created"),
        };
        var project = await management.CreateProjectAsync(user, organization.Id, "main", CancellationToken.None) switch
        {
            Project value => value,
            _ => throw new InvalidOperationException("the catalog fixture was not created"),
        };
        var database = await management.CreateDatabaseAsync(user, organization.Id, project.Id, "orders", CancellationToken.None) switch
        {
            ManagedDatabase value => value,
            _ => throw new InvalidOperationException("the catalog fixture was not created"),
        };
        var key = await primary.IssueAsync(organization.Id, Scopes);
        using var channel = peers.CreateChannel(primary.PeerAddress!);
        var client = new CatalogForwarding.CatalogForwardingClient(channel);
        var before = await primary.Services.GetRequiredService<ISystemReplication>().GetHeadAsync(CancellationToken.None);

        // Endpoints the primary serves to nobody's followers answer 404 unexecuted: the loopback listener's, probes and the
        // data plane, even with a valid key.
        var documents = Request("GET", $"/v1/projects/{project.Id}/databases/orders/collections");
        documents.Headers.Add(new ForwardedHeader { Name = "Authorization", Value = "Bearer " + key });
        foreach (var request in new[]
        {
            Request("POST", "/admin/api-keys", JsonSerializer.SerializeToUtf8Bytes(new { name = "forwarded", scopes = Scopes, organizationId = organization.Id })),
            Request("PUT", $"/v1/management/organizations/{organization.Id}/members/{EntityIds.New()}", JsonSerializer.SerializeToUtf8Bytes(new { email = "m@example.com" })),
            Request("GET", "/healthz"),
            Request("GET", "/readyz"),
            documents,
        })
        {
            var (head, body) = await ForwardAsync(client, request);
            await Assert.That(head.Status).IsEqualTo(404U).Because(request.Path);
            await Assert.That(JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unknown_route");
        }

        // The Management API authenticates as on the client listener: no token, or the loopback listener's credential.
        var service = Request("GET", "/v1/management/organizations");
        service.Headers.Add(new ForwardedHeader { Name = "Authorization", Value = "Bearer " + ManagementApi.ServiceCredential });
        service.Headers.Add(new ForwardedHeader { Name = "X-TerraBase-User-Id", Value = user });
        foreach (var request in new[] { Request("GET", "/v1/management/organizations"), service })
        {
            var (head, _) = await ForwardAsync(client, request);
            await Assert.That(head.Status).IsEqualTo(401U);
            await Assert.That(head.Headers.Single(header => header.Name == "X-TerraBase-Region").Value).IsEqualTo("follower-region");
        }

        // A request is answered as the follower's region, pin included.
        var pinned = WithHeader(Request("GET", "/v1/management/organizations"), "X-TerraScale-Region-Pin", "test-region");
        var (misdirected, _) = await ForwardAsync(client, pinned);
        await Assert.That(misdirected.Status).IsEqualTo(421U);
        var after = await primary.Services.GetRequiredService<ISystemReplication>().GetHeadAsync(CancellationToken.None);
        await Assert.That(after.Sequence).IsEqualTo(before.Sequence);
        await Assert.That(misdirected.Origin.Span.SequenceEqual(after.Origin.Span)).IsTrue();

        // A forwarded read reports the answering region without changing the database's persisted home.
        var read = WithHeader(Request("GET", $"/v1/management/organizations/{organization.Id}/projects/{project.Id}/databases/{database.Id}"),
            "Authorization", "Bearer " + issuer.Mint(user));
        read = WithHeader(read, "X-TerraScale-Region-Pin", "follower-region");
        var (readHead, readBody) = await ForwardAsync(client, read);
        await Assert.That(readHead.Status).IsEqualTo(200U);
        await Assert.That(readHead.Headers.Single(header => header.Name == "X-TerraBase-Region").Value).IsEqualTo("follower-region");
        using var saved = JsonDocument.Parse(readBody);
        await Assert.That(saved.RootElement.GetProperty("region").GetString()).IsEqualTo("follower-region");
        await Assert.That(saved.RootElement.GetProperty("homeRegion").GetString()).IsEqualTo("test-region");

        // Requests no HTTP server would have accepted, and tampered bodies, are refused before anything runs.
        var envelope = ApplicationEnvelope.Encode("{\"name\":\"x\"}"u8).ToArray();
        var tampered = envelope.ToArray();
        tampered[^1] ^= 1;
        var invalid = new (string Case, ForwardRequest Request)[]
        {
            ("protocol", Request("GET", "/", protocol: 1)),
            ("region", Request("GET", "/", region: "Not A Region")),
            ("method", Request("GE T", "/")),
            ("path", Request("GET", "v1/management/organizations")),
            ("path control", Request("GET", "/v1\r\nX: 1")),
            ("query", Request("GET", "/", query: "a=1")),
            ("header name", WithHeader(Request("GET", "/"), "Bad Name", "1")),
            ("header value", WithHeader(Request("GET", "/"), "X-Test", "1\r\nX-Injected: 1")),
            ("no length", Request("POST", "/", envelope: envelope, length: null)),
            ("two lengths", WithHeader(Request("POST", "/", envelope: envelope, length: 12), "Content-Length", "12")),
            ("long length", Request("POST", "/", envelope: envelope, length: 13)),
            ("short length", Request("POST", "/", envelope: envelope, length: 11)),
            ("tampered body", Request("POST", "/", envelope: tampered, length: 12)),
        };
        foreach (var (name, request) in invalid)
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await ForwardAsync(client, request));
            await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.InvalidArgument).Because(name);
        }
    }

    [Test]
    public async Task A_forwarded_request_answers_once_the_follower_applied_the_primary_head_or_after_the_apply_timeout()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var origin = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var initial = JournalDigest.Initial(origin);
        var first = new DocumentChange("catalog_test", "first", 3, Encoding.UTF8.GetBytes("1"));
        var firstDigest = JournalDigest.Compute(origin, 1, initial, [first]);
        var second = new DocumentChange("catalog_test", "second", 4, Encoding.UTF8.GetBytes("2"));
        // Set once the fake primary sent its whole response to the first and the second forwarded request, once the third
        // reached it, and once it sent the fourth's head.
        var answered = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var lateReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateHead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var fake = await FakePeer.StartCatalogAsync(peers, async (_, responses, context) =>
        {
            await responses.WriteAsync(new FollowResponse { Welcome = new CatalogWelcome { Protocol = CatalogProtocol.Version, Origin = ByteString.CopyFrom(origin), Head = 1 } });
            await responses.WriteAsync(new FollowResponse { SnapshotBegin = new SnapshotBegin { Sequence = 0, Digest = ByteString.CopyFrom(initial) } });
            await WriteGroupAsync(responses, 1, [first], firstDigest);
            await responses.WriteAsync(new FollowResponse { SnapshotEnd = new SnapshotEnd { Sequence = 1, Digest = ByteString.CopyFrom(firstDigest), CommitOffset = 3 } });
            await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = 1 } });
            await deliver.Task.WaitAsync(context.CancellationToken);
            await WriteGroupAsync(responses, 2, [second], JournalDigest.Compute(origin, 2, firstDigest, [second]));
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }, async (_, responses, context) =>
        {
            // The first request wrote group 2, which the follower gets only after the primary answered; the second wrote group
            // 3, which never comes; the third is never answered; the fourth gets a head for group 4, which never comes, when
            // the test says so, and no body.
            var call = Interlocked.Increment(ref calls);
            if (call == 3)
            {
                answered[2].TrySetResult();
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            }

            if (call == 4)
            {
                lateReached.TrySetResult();
                await lateHead.Task.WaitAsync(context.CancellationToken);
                await responses.WriteAsync(new ForwardResponse
                {
                    Head = new ForwardHead { Status = 201, BodyLength = 2, Origin = ByteString.CopyFrom(origin), Sequence = 4 },
                });
                answered[3].TrySetResult();
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            }

            await responses.WriteAsync(new ForwardResponse
            {
                Head = new ForwardHead { Status = 201, BodyLength = 2, Origin = ByteString.CopyFrom(origin), Sequence = call == 1 ? 2UL : 3UL },
            });
            await responses.WriteAsync(new ForwardResponse { Body = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode("{}"u8)) });
            answered[call - 1].TrySetResult();
        });
        await using var follower = await StartFollowerAsync(peers, tls, issuer, fake.Address, services => services.AddSingleton<TimeProvider>(time));
        await ReadyAsync(follower);
        using var client = tls.CreateClient(follower.ClientAddress!, issuer.Mint(EntityIds.New().ToString()));

        // Group 2 leaves the primary only after its whole response did: the follower answers once it applied it.
        var answer = client.PostAsJsonAsync(Relative("/v1/management/organizations"), new { name = "Waits" });
        await answered[0].Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(answer.IsCompleted).IsFalse();
        deliver.SetResult();
        using (var response = await answer)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(await CountAsync(follower, "catalog_test")).IsEqualTo(2);
        }

        // Group 3 never comes: the answer takes the whole apply timeout of the follower's clock.
        var lagging = client.PostAsJsonAsync(Relative("/v1/management/organizations"), new { name = "Lags" });
        await answered[1].Task.WaitAsync(TimeSpan.FromSeconds(30));
        var advanced = TimeSpan.Zero;
        await TerraBaseTestServer.EventuallyAsync(_ =>
        {
            if (!lagging.IsCompleted)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                advanced += TimeSpan.FromSeconds(1);
            }

            return Task.FromResult(lagging.IsCompleted);
        });
        using (var response = await lagging)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(advanced).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
        }

        // A primary that never answers holds the request only until the forwarding deadline.
        var stalled = client.PostAsJsonAsync(Relative("/v1/management/organizations"), new { name = "Stalls" });
        await answered[2].Task.WaitAsync(TimeSpan.FromSeconds(30));
        advanced = TimeSpan.Zero;
        await TerraBaseTestServer.EventuallyAsync(_ =>
        {
            if (!stalled.IsCompleted)
            {
                time.Advance(TimeSpan.FromSeconds(10));
                advanced += TimeSpan.FromSeconds(10);
            }

            return Task.FromResult(stalled.IsCompleted);
        });
        using (var response = await stalled)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(await CodeAsync(response)).IsEqualTo("catalog_primary_unavailable");
            await Assert.That(advanced).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(60));
        }

        // A head 58 seconds in starts a 5-second apply wait, but the forwarding deadline still ends the request at 60.
        var late = client.PostAsJsonAsync(Relative("/v1/management/organizations"), new { name = "Late" });
        await lateReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(58));
        lateHead.SetResult();
        await answered[3].Task.WaitAsync(TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(3));
        using (var response = await late.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(await CodeAsync(response)).IsEqualTo("catalog_primary_unavailable");
        }

        await Assert.That(await follower.MetricAsync("terrabase_catalog_forwarded_total", "result=\"answered\"")).IsEqualTo(1);
        await Assert.That(await follower.MetricAsync("terrabase_catalog_forwarded_total", "result=\"lagging\"")).IsEqualTo(1);
    }

    [Test]
    public async Task A_follower_forwards_requests_as_they_arrived_relays_the_response_and_refuses_a_broken_one()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        var origin = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var initial = JournalDigest.Initial(origin);
        var requests = new List<ForwardRequest>();
        var body = Encoding.UTF8.GetBytes(new string('x', 3 * 1024 * 1024));
        await using var fake = await FakePeer.StartCatalogAsync(peers, async (_, responses, context) =>
        {
            await responses.WriteAsync(new FollowResponse { Welcome = new CatalogWelcome { Protocol = CatalogProtocol.Version, Origin = ByteString.CopyFrom(origin), Head = 0 } });
            await responses.WriteAsync(new FollowResponse { SnapshotBegin = new SnapshotBegin { Sequence = 0, Digest = ByteString.CopyFrom(initial) } });
            await responses.WriteAsync(new FollowResponse { SnapshotEnd = new SnapshotEnd { Sequence = 0, Digest = ByteString.CopyFrom(initial), CommitOffset = 1 } });
            await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = 0 } });
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }, async (request, responses, _) =>
        {
            lock (requests)
            {
                requests.Add(request);
            }

            var head = new ForwardHead { Status = 200, Origin = ByteString.CopyFrom(origin) };
            switch (request.Path)
            {
                case "/v1/management/organizations/large":
                    // A body larger than one envelope, with a header to relay and a connection header not to.
                    head.BodyLength = (ulong)body.Length;
                    head.Headers.Add(new ForwardedHeader { Name = "X-Relayed", Value = "1" });
                    head.Headers.Add(new ForwardedHeader { Name = "Connection", Value = "close" });
                    await responses.WriteAsync(new ForwardResponse { Head = head });
                    for (var offset = 0; offset < body.Length; offset += 1024 * 1024)
                    {
                        await responses.WriteAsync(new ForwardResponse
                        {
                            Body = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(body.AsSpan(offset, Math.Min(1024 * 1024, body.Length - offset)))),
                        });
                    }

                    break;
                case "/v1/management/organizations/tampered":
                    head.BodyLength = 2;
                    await responses.WriteAsync(new ForwardResponse { Head = head });
                    var envelope = ApplicationEnvelope.Encode("{}"u8).ToArray();
                    envelope[^1] ^= 1;
                    await responses.WriteAsync(new ForwardResponse { Body = ByteString.CopyFrom(envelope) });
                    break;
                case "/v1/management/organizations/empty-then-fails":
                    await responses.WriteAsync(new ForwardResponse { Head = head });
                    throw new RpcException(new Status(StatusCode.Internal, "failed after its head"));
                case "/v1/management/organizations/body-then-fails":
                    head.BodyLength = 2;
                    await responses.WriteAsync(new ForwardResponse { Head = head });
                    await responses.WriteAsync(new ForwardResponse { Body = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode("{}"u8)) });
                    throw new RpcException(new Status(StatusCode.Internal, "failed after its body"));
                case "/v1/management/organizations/trailing":
                    head.BodyLength = 2;
                    await responses.WriteAsync(new ForwardResponse { Head = head });
                    await responses.WriteAsync(new ForwardResponse { Body = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode("{}"u8)) });
                    await responses.WriteAsync(new ForwardResponse { Head = head });
                    break;
                case "/v1/management/organizations/large-then-fails":
                    head.BodyLength = (ulong)body.Length;
                    await responses.WriteAsync(new ForwardResponse { Head = head });
                    for (var offset = 0; offset < body.Length; offset += 1024 * 1024)
                    {
                        await responses.WriteAsync(new ForwardResponse
                        {
                            Body = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(body.AsSpan(offset, Math.Min(1024 * 1024, body.Length - offset)))),
                        });
                    }

                    throw new RpcException(new Status(StatusCode.Internal, "failed after its whole body"));
                case "/v1/management/organizations/headless":
                    await responses.WriteAsync(new ForwardResponse { Body = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode("{}"u8)) });
                    break;
                case "/v1/management/organizations/hop":
                    // Connection names a header that must stay between the primary and the follower.
                    head.Headers.Add(new ForwardedHeader { Name = "Connection", Value = "keep-alive, X-Secret" });
                    head.Headers.Add(new ForwardedHeader { Name = "X-Secret", Value = "1" });
                    head.Headers.Add(new ForwardedHeader { Name = "X-Public", Value = "1" });
                    await responses.WriteAsync(new ForwardResponse { Head = head });
                    break;
                case "/v1/management/organizations/invalid":
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid"));
                default:
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "busy"));
            }
        });
        await using var follower = await StartFollowerAsync(peers, tls, issuer, fake.Address);
        await ReadyAsync(follower);
        var token = issuer.Mint(EntityIds.New().ToString());
        using var client = tls.CreateClient(follower.ClientAddress!, token);
        client.DefaultRequestHeaders.Add("X-TerraScale-Region-Pin", "test-region");

        // The request goes as it arrived, without connection headers or the pin; the response comes back whole, without them either.
        using (var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"name\":\"large\"}")))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var large = await client.PatchAsync(Relative("/v1/management/organizations/large?expand=1"), content);
            await Assert.That(large.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await large.Content.ReadAsStringAsync());
            await Assert.That(large.Headers.GetValues("X-Relayed").Single()).IsEqualTo("1");
            await Assert.That(large.Headers.ConnectionClose ?? false).IsFalse();
            await Assert.That((await large.Content.ReadAsByteArrayAsync()).SequenceEqual(body)).IsTrue();
        }

        var sent = requests.Single();
        await Assert.That((sent.Method, sent.Path, sent.Query, sent.Region)).IsEqualTo(("PATCH", "/v1/management/organizations/large", "?expand=1", "test-region"));
        await Assert.That(sent.Headers.Single(header => header.Name == "Authorization").Value).IsEqualTo("Bearer " + token);
        await Assert.That(sent.Headers.Single(header => header.Name == "Content-Length").Value).IsEqualTo("16");
        await Assert.That(sent.Headers.Any(header => header.Name == "X-TerraScale-Region-Pin")).IsFalse();
        await Assert.That(Encoding.UTF8.GetString(ApplicationEnvelope.Decode(sent.Body.Span, 16))).IsEqualTo("{\"name\":\"large\"}");

        // Headers HTTP/1.1's Connection names stay on their hop, both ways.
        using (var hop = new HttpRequestMessage(HttpMethod.Get, Relative("/v1/management/organizations/hop"))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        })
        {
            hop.Headers.Connection.Add("X-Hop");
            hop.Headers.Add("X-Hop", "1");
            hop.Headers.Add("X-Kept", "1");
            using var relayed = await client.SendAsync(hop);
            await Assert.That(relayed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(relayed.Headers.Contains("X-Secret")).IsFalse();
            await Assert.That(relayed.Headers.GetValues("X-Public").Single()).IsEqualTo("1");
        }

        var forwardedHop = requests.Single(request => request.Path == "/v1/management/organizations/hop");
        await Assert.That(forwardedHop.Headers.Any(header => header.Name == "X-Hop")).IsFalse();
        await Assert.That(forwardedHop.Headers.Single(header => header.Name == "X-Kept").Value).IsEqualTo("1");

        // A primary that refuses, answers without a head, sends a tampered body, more frames than its head declared, or fails
        // after its response, is unavailable while nothing went out; one that cannot serve the request at all answers 400; one
        // that fails once part of the body went out resets the connection.
        foreach (var path in new[] { "busy", "headless", "tampered", "empty-then-fails", "body-then-fails", "trailing" })
        {
            using var unavailable = await client.GetAsync(Relative($"/v1/management/organizations/{path}"));
            await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(unavailable.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
            await Assert.That(await CodeAsync(unavailable)).IsEqualTo("catalog_primary_unavailable");
        }

        using (var invalid = await client.GetAsync(Relative("/v1/management/organizations/invalid")))
        {
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await CodeAsync(invalid)).IsEqualTo("invalid_request");
        }

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.GetByteArrayAsync(Relative("/v1/management/organizations/large-then-fails")));
        await Assert.That(await follower.MetricAsync("terrabase_catalog_forwarded_total", "result=\"unavailable\"")).IsEqualTo(7);
        await Assert.That(await follower.MetricAsync("terrabase_catalog_forwarded_total", "result=\"answered\"")).IsEqualTo(2);
    }

    [Test]
    public async Task A_follower_answers_503_while_its_primary_cannot_be_reached_and_413_for_a_body_it_cannot_forward()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        await using var follower = await StartFollowerAsync(peers, tls, issuer, new Uri($"https://localhost:{TerraBaseTestServer.FreePort()}"));
        using var client = tls.CreateClient(follower.ClientAddress!, issuer.Mint(EntityIds.New().ToString()));

        using (var unreachable = await client.PostAsJsonAsync(Relative("/v1/management/organizations"), new { name = "Partitioned" }))
        {
            await Assert.That(unreachable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(unreachable.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
            await Assert.That(await CodeAsync(unreachable)).IsEqualTo("catalog_primary_unavailable");
        }

        using var large = new ByteArrayContent(new byte[(4 * 1024 * 1024) + 1]);
        using (var tooLarge = await client.PostAsync(Relative("/v1/management/organizations"), large))
        {
            await Assert.That(tooLarge.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
            await Assert.That(await CodeAsync(tooLarge)).IsEqualTo("payload_too_large");
        }

        await Assert.That(await follower.MetricAsync("terrabase_catalog_forwarded_total", "result=\"unavailable\"")).IsEqualTo(1);
    }

    [Test]
    public async Task A_management_request_a_follower_relays_is_metered_once_by_the_primary_that_resolved_its_database()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        await using var primary = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            tls.Apply(settings);
            TestIssuer.Configure(settings);
            settings["region"] = "gru-1";
        }, issuer.Route);
        await using var follower = await StartFollowerAsync(peers, tls, issuer, primary.PeerAddress!, region: "gru-1");
        await ReadyAsync(follower);
        var faker = new Faker();
        var project = Ulid.NewUlid().ToString();
        var token = issuer.Mint(EntityIds.New().ToString(), claims =>
        {
            claims.Claims["project_id"] = project;
            claims.Claims["scope"] = "databases:read databases:write";
        });
        using var relayed = tls.CreateClient(follower.ClientAddress!, token);
        (await relayed.PostAsJsonAsync(Relative($"/v1/projects/{project}/databases"), new { name = "main" })).EnsureSuccessStatusCode();
        var reads = faker.Random.Int(1, 3);
        for (var i = 0; i < reads; i++)
        {
            (await relayed.GetAsync(Relative($"/v1/projects/{project}/databases/main"))).EnsureSuccessStatusCode();
        }

        // Neither the relaying follower (no resolved database) nor a second observation on the primary may count them: deliver
        // whatever the follower metered before reading the primary's totals.
        await follower.Services.GetServices<IHostedService>().OfType<UsageFlushService>().Single().FlushAsync(CancellationToken.None);
        using var direct = tls.CreateClient(primary.ClientAddress!, token);
        var analytics = await direct.GetFromJsonAsync<JsonElement>(Relative($"/v1/projects/{project}/databases/main/analytics?range=1h&region=gru-1"));
        var gets = analytics.GetProperty("items")[0].GetProperty("series").EnumerateArray()
            .Sum(point => point.GetProperty("requests").TryGetProperty("get", out var count) ? count.GetInt64() : 0);
        await Assert.That(gets).IsEqualTo(reads);
    }

    [Test]
    public async Task A_primary_without_memory_for_a_batchs_measurements_still_adds_its_totals()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        await using var primary = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            tls.Apply(settings);
            TestIssuer.Configure(settings);
        }, services =>
        {
            issuer.Route(services);
            var registration = services.Single(service => service.ServiceType == typeof(IMemoryBudget));
            services.Remove(registration);
            services.AddSingleton(provider => new MeasurementMemory((IMemoryBudget)registration.ImplementationFactory!(provider)));
            services.AddSingleton<IMemoryBudget>(provider => provider.GetRequiredService<MeasurementMemory>());
        });
        await using var follower = await StartFollowerAsync(peers, tls, issuer, primary.PeerAddress!);
        await ReadyAsync(follower);
        using var dashboard = tls.CreateClient(primary.ClientAddress!, issuer.Mint(EntityIds.New().ToString()));
        var organization = await CreatedIdAsync(dashboard, "/v1/management/organizations", new { name = "Measured" });
        var project = await CreatedIdAsync(dashboard, $"/v1/management/organizations/{organization}/projects", new { name = "main" });
        var database = await CreatedIdAsync(dashboard, $"/v1/management/organizations/{organization}/projects/{project}/databases", new { name = "orders" });
        var hour = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600;
        var meters = new Dictionary<string, UsageReading>();
        foreach (var peer in new[] { "sfo-1", "iad-1", "fra-1" })
        {
            foreach (var kind in new[] { "delta", "anti_entropy", "handoff" })
            {
                foreach (var direction in new[] { "sent", "received" })
                {
                    var dimension = new UsageDimension("replication_bytes", "gru-1", peer, $"{kind}:{direction}");
                    meters[dimension.Key] = new(dimension, 1, 512);
                }
            }
        }

        // Measurements past 1 KiB need more than the 4 KiB a batch of totals reserves; this primary has no memory for them and
        // refuses such a batch before it adds any of it.
        primary.Services.GetRequiredService<MeasurementMemory>().Refuse = true;
        var probe = Batch(ByteString.CopyFrom(EntityIds.New().ToByteArray()), 1, organization, project, database, hour, reads: 1, sender: "probe");
        probe.Counts[0].Measurements = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
            new OpCounts(0, 0, meters), TerraBase.Hosting.TerraBaseJsonContext.Default.OpCounts));
        await Assert.That(await follower.Services.GetRequiredService<ICatalogPrimaryClient>().AddUsageAsync(probe, CancellationToken.None) is QuotaExceeded).IsTrue();

        // The follower then sends the batch's totals alone under its sequence: the reads count, the measurements are dropped.
        var reads = new Faker().Random.Int(1, 9);
        var owner = new DatabaseOwner(organization, project, database);
        var local = follower.Services.GetRequiredService<UsageMeter>();
        local.Add(new UsageKey(owner, hour), new OpCounts(reads, 0));
        local.Add(new UsageKey(owner, hour), new OpCounts(0, 0, meters));
        await follower.Services.GetServices<IHostedService>().OfType<UsageFlushService>().Single().FlushAsync(CancellationToken.None);
        var meter = primary.Services.GetRequiredService<UsageMeter>();
        await meter.FlushAsync(CancellationToken.None);
        var (lifetime, _) = await meter.GetObservationsAsync(
            new TenantContext(organization, PlanTier.Hobby, project, database), new UsageWindow(UsageWindow.Hour, hour, 1), CancellationToken.None) switch
        {
            UsageCounts counts => counts,
            Unavailable error => throw new InvalidOperationException(error.Message),
        };
        await Assert.That(lifetime.Reads).IsEqualTo(reads);
        await Assert.That(lifetime.Meters is null || lifetime.Meters.Keys.All(key => !meters.ContainsKey(key))).IsTrue();
    }

    [Test]
    public async Task A_followers_reads_and_writes_reach_its_primarys_usage_once_also_when_an_acknowledgement_is_lost()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        await using var primary = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            tls.Apply(settings);
            TestIssuer.Configure(settings);
        }, issuer.Route);
        // The primary adds the follower's first batch, but its acknowledgement never arrives. The follower's clock stands
        // still, so only the explicit flushes below send.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        TerraBaseTestServer? follower = await StartFollowerAsync(peers, tls, issuer, primary.PeerAddress!, services => services
            .AddSingleton<TimeProvider>(time)
            .AddSingleton<CatalogPrimaryClient>()
            .AddSingleton<LostFirstAcknowledgement>()
            .AddSingleton<ICatalogPrimaryClient>(provider => provider.GetRequiredService<LostFirstAcknowledgement>()));
        try
        {
            await ReadyAsync(follower);
            var user = EntityIds.New().ToString();
            using var dashboard = tls.CreateClient(primary.ClientAddress!, issuer.Mint(user));
            var organization = await CreatedIdAsync(dashboard, "/v1/management/organizations", new { name = "Metered" });
            var project = await CreatedIdAsync(dashboard, $"/v1/management/organizations/{organization}/projects", new { name = "main" });
            var database = await CreatedIdAsync(dashboard, $"/v1/management/organizations/{organization}/projects/{project}/databases", new { name = "orders" });
            string key;
            using (var issued = await dashboard.PostAsJsonAsync(
                Relative($"/v1/management/organizations/{organization}/projects/{project}/api-keys"), new { name = "app", scopes = Scopes }))
            {
                key = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
            }

            // N reads and M writes served by the follower, once it applied the key and the database.
            using var data = tls.CreateClient(follower.ClientAddress!, key);
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                using var known = await data.GetAsync(Relative($"/v1/projects/{project}/databases/orders/collections"), token);
                return known.StatusCode == HttpStatusCode.OK;
            });
            var orders = $"/v1/projects/{project}/databases/orders/collections";
            (await data.PostAsJsonAsync(Relative(orders), new { name = "items" })).EnsureSuccessStatusCode();
            var faker = new Faker();
            var (writes, reads, later) = (faker.Random.Int(1, 4), faker.Random.Int(1, 4), faker.Random.Int(1, 4));
            for (var i = 0; i < writes; i++)
            {
                (await data.PutAsJsonAsync(Relative($"{orders}/items/documents/by-key?id={ManagementApi.DocumentId($"d{i}")}"), new { total = i })).EnsureSuccessStatusCode();
            }

            async Task ReadAsync(int count)
            {
                for (var i = 0; i < count; i++)
                {
                    (await data.GetAsync(Relative($"{orders}/items/documents/by-key?id={ManagementApi.DocumentId("d0")}"))).EnsureSuccessStatusCode();
                }
            }

            await ReadAsync(reads);
            var usagePath = $"/v1/management/organizations/{organization}/projects/{project}/databases/{database}/usage";
            async Task<(long Reads, long Writes)> UsageAsync(HttpClient client)
            {
                var usage = await client.GetFromJsonAsync<JsonElement>(Relative(usagePath));
                return (usage.GetProperty("reads").GetInt64(), usage.GetProperty("writes").GetInt64());
            }

            var flush = follower.Services.GetServices<IHostedService>().OfType<UsageFlushService>().Single();
            var sent = follower.Services.GetRequiredService<LostFirstAcknowledgement>();
            await flush.FlushAsync(CancellationToken.None);
            await Assert.That(await UsageAsync(dashboard)).IsEqualTo((reads, writes));

            // The primary saves the batch and it replicates here before its acknowledgement does: a usage read served by the
            // follower counts it once, from the replicated totals, though the batch is still unsent here.
            await primary.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);
            var local = follower.Services.GetRequiredService<UsageMeter>();
            var tenant = new TenantContext(organization, PlanTier.Hobby, project, database);
            var thisHour = new UsageWindow(UsageWindow.Hour, time.GetUtcNow().ToUnixTimeSeconds() / 3600, 1);
            await TerraBaseTestServer.EventuallyAsync(async token => await local.GetAsync(tenant, thisHour, token) switch
            {
                UsageCounts counts => counts.Lifetime.Reads >= reads,
                Unavailable => false,
                null => throw new InvalidOperationException("the usage result has no case"),
            });
            var replicatedCounts = await local.GetAsync(tenant, thisHour, CancellationToken.None) switch
            {
                UsageCounts counts => counts.Lifetime,
                Unavailable => throw new InvalidOperationException("the replicated usage catalog was unavailable"),
                null => throw new InvalidOperationException("the usage result has no case"),
            };
            await Assert.That(replicatedCounts).IsEqualTo(new OpCounts(reads, writes));

            // A usage read served by the follower itself settles nothing of the unacknowledged batch. More reads arrive; the
            // next flush sends that batch again with its sequence, which the primary adds once, and the one after the new reads.
            var hour = time.GetUtcNow().ToUnixTimeSeconds() / 3600;
            var meter = follower.Services.GetRequiredService<UsageMeter>();
            _ = await meter.GetAsync(new TenantContext(organization, PlanTier.Hobby, project, database), new UsageWindow(UsageWindow.Hour, hour, 1), CancellationToken.None);
            await ReadAsync(later);
            await flush.FlushAsync(CancellationToken.None);
            await flush.FlushAsync(CancellationToken.None);
            await Assert.That(await UsageAsync(dashboard)).IsEqualTo((reads + later, writes));
            await Assert.That(sent.Sequences).IsEquivalentTo([1UL, 1UL, 2UL], TUnit.Assertions.Enums.CollectionOrdering.Matching);

            // The follower's client reports a batch the primary refuses, here an hour two days ahead of its clock, as refused.
            var ahead = Batch(ByteString.CopyFrom(EntityIds.New().ToByteArray()), 1, organization, project, database, hour + 48, reads: 1, sender: "ahead");
            await Assert.That(await follower.Services.GetRequiredService<CatalogPrimaryClient>().AddUsageAsync(ahead, CancellationToken.None) is Validation).IsTrue();

            // Byte aggregates outgrow the operation-count bound (10^12): an hour of 20 GB storage samples folded during an outage is valid.
            var folded = Batch(ByteString.CopyFrom(Ulid.NewUlid().ToByteArray()), 1, organization, project, database, hour, reads: 0, sender: "folded");
            var storage = new UsageDimension("storage_bytes", "gru-1", "", "sample");
            folded.Counts[0].Measurements = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
                new OpCounts(0, 0, new Dictionary<string, UsageReading> { [storage.Key] = new(storage, 60, 60 * 20_000_000_000L) }),
                TerraBase.Hosting.TerraBaseJsonContext.Default.OpCounts));
            await Assert.That(await follower.Services.GetRequiredService<CatalogPrimaryClient>().AddUsageAsync(folded, CancellationToken.None) is Done).IsTrue();

            // Two accepted byte readings past half the 64-bit range merge at its saturation, never into negative bandwidth.
            var sentBytes = new UsageDimension("client_bytes", "gru-1", "", "sent");
            foreach (var sender in new[] { "large-a", "large-b" })
            {
                var large = Batch(ByteString.CopyFrom(Ulid.NewUlid().ToByteArray()), 1, organization, project, database, hour, reads: 0, sender: sender);
                large.Counts[0].Measurements = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
                    new OpCounts(0, 0, new Dictionary<string, UsageReading> { [sentBytes.Key] = new(sentBytes, (long.MaxValue / 2) + 1) }),
                    TerraBase.Hosting.TerraBaseJsonContext.Default.OpCounts));
                await Assert.That(await follower.Services.GetRequiredService<CatalogPrimaryClient>().AddUsageAsync(large, CancellationToken.None) is Done).IsTrue();
            }

            await primary.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);
            var (merged, _) = await primary.Services.GetRequiredService<UsageMeter>().GetObservationsAsync(
                new TenantContext(organization, PlanTier.Hobby, project, database), new UsageWindow(UsageWindow.Hour, hour, 1), CancellationToken.None) switch
            {
                UsageCounts counts => counts,
                Unavailable error => throw new InvalidOperationException(error.Message),
                null => throw new System.Diagnostics.UnreachableException(),
            };
            await Assert.That(merged.Meters![sentBytes.Key].Count).IsEqualTo(long.MaxValue);

            // A clean shutdown sends every pending batch, not only the first 4,096 database hours: here 4,097, one of them this
            // database's. The others belong to a project the primary does not have, which it checks once and drops.
            meter.Add(new UsageKey(new DatabaseOwner(organization, project, database), hour), new OpCounts(1, 0));
            var gone = EntityIds.New().ToString();
            for (var i = 0; i < UsageMeter.MaxBatch; i++)
            {
                meter.Add(new UsageKey(new DatabaseOwner(organization, gone, EntityIds.New().ToString()), hour), new OpCounts(1, 0));
            }

            await follower.DisposeAsync();
            follower = null;
            await Assert.That(await UsageAsync(dashboard)).IsEqualTo((reads + later + 1, writes));
            await Assert.That(sent.Sequences).IsEquivalentTo([1UL, 1UL, 2UL, 3UL, 4UL], TUnit.Assertions.Enums.CollectionOrdering.Matching);

            // A batch at or below the highest sequence of its sender's process counts once, also when an attempt arrives after
            // a later batch; a new process starts over. Only valid batches are taken.
            var epoch = ByteString.CopyFrom(EntityIds.New().ToByteArray());
            using var channel = peers.CreateChannel(primary.PeerAddress!);
            var client = new CatalogUsage.CatalogUsageClient(channel);
            var (beforeReads, beforeWrites) = await UsageAsync(dashboard);
            await client.AddAsync(Batch(epoch, 1, organization, project, database, hour, reads: 7));
            await client.AddAsync(Batch(epoch, 2, organization, project, database, hour, reads: 2));
            await client.AddAsync(Batch(epoch, 1, organization, project, database, hour, reads: 7));
            // A restarted process (a new epoch) starts over; a delayed attempt of the previous process still counts once.
            var restarted = ByteString.CopyFrom(EntityIds.New().ToByteArray());
            await client.AddAsync(Batch(restarted, 1, organization, project, database, hour, reads: 1));
            await client.AddAsync(Batch(epoch, 2, organization, project, database, hour, reads: 2));
            await client.AddAsync(Batch(restarted, 1, organization, project, database, hour, reads: 1));
            await Assert.That(await UsageAsync(dashboard)).IsEqualTo((beforeReads + 7 + 2 + 1, beforeWrites));
            // A day-only count adds to its day and the lifetime totals, never to the hour it is filed at.
            var midnight = DateTimeOffset.FromUnixTimeSeconds(hour / 24 * 24 * 3600).ToString("yyyy-MM-dd'T'HH", System.Globalization.CultureInfo.InvariantCulture);
            async Task<long> PeriodReadsAsync(string query)
            {
                return (await dashboard.GetFromJsonAsync<JsonElement>(Relative(usagePath + query))).GetProperty("period").GetProperty("reads").GetInt64();
            }
            var (hourBefore, dayBefore) = (await PeriodReadsAsync($"?from={midnight}:00:00Z&to={midnight}:59:59Z"), await PeriodReadsAsync("?granularity=day"));
            var dayOnly = Batch(epoch, 3, organization, project, database, hour / 24 * 24, reads: 4);
            dayOnly.Counts[0].Scope = Features.Replication.V1.UsageScope.Day;
            await client.AddAsync(dayOnly);
            await Assert.That(await UsageAsync(dashboard)).IsEqualTo((beforeReads + 7 + 2 + 1 + 4, beforeWrites));
            await Assert.That(await PeriodReadsAsync($"?from={midnight}:00:00Z&to={midnight}:59:59Z")).IsEqualTo(hourBefore);
            await Assert.That(await PeriodReadsAsync("?granularity=day")).IsEqualTo(dayBefore + 4);
            foreach (var invalid in new[]
            {
                Batch(ByteString.CopyFromUtf8("short"), 3, organization, project, database, hour, reads: 1),
                Batch(epoch, 0, organization, project, database, hour, reads: 1),
                Batch(epoch, 3, organization, project, database, hour + 25, reads: 1),
                Batch(epoch, 3, organization, project, database, long.MaxValue, reads: 1),
                Batch(epoch, 3, organization, project, database, hour, reads: UsageMeter.MaxCount + 1),
                Batch(epoch, 3, organization, project, database, hour, reads: 1, sender: ""),
                Scoped(Batch(epoch, 3, organization, project, database, hour / 24 * 24 + 1, reads: 1), Features.Replication.V1.UsageScope.Day),
                Scoped(Batch(epoch, 3, organization, project, database, hour, reads: 1), Features.Replication.V1.UsageScope.Lifetime),
                Scoped(Batch(epoch, 3, organization, project, database, 0, reads: 1), (Features.Replication.V1.UsageScope)7),
            })
            {
                var refused = await Assert.ThrowsAsync<RpcException>(async () => await client.AddAsync(invalid));
                await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
            }

            // A batch full of databases this catalog does not name holds no memory for them: only the real one counts.
            var flood = Batch(epoch, 4, organization, project, database, hour, reads: 1);
            for (var i = 1; i < UsageMeter.MaxBatch; i++)
            {
                flood.Counts.Add(new UsageCount { OrganizationId = organization, ProjectId = project, DatabaseId = EntityIds.New().ToString(), Hour = hour, Reads = 1 });
            }

            await client.AddAsync(flood);
            await Assert.That(primary.Services.GetRequiredService<UsageMeter>().PendingDatabases).IsLessThanOrEqualTo(1);
            await Assert.That(await UsageAsync(dashboard)).IsEqualTo((beforeReads + 7 + 2 + 1 + 4 + 1, beforeWrites));
        }
        finally
        {
            if (follower is not null)
            {
                await follower.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task A_batch_sent_again_after_its_primary_restarted_counts_once_since_its_sequence_was_saved_with_its_counts()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        var directory = Directory.CreateTempSubdirectory("terrabase-usage-senders-").FullName;
        try
        {
            void Configure(Dictionary<string, string> settings)
            {
                peers.Apply(settings);
                tls.Apply(settings);
                TestIssuer.Configure(settings);
                settings["data-directory"] = directory;
            }

            var user = EntityIds.New().ToString();
            var epoch = ByteString.CopyFrom(EntityIds.New().ToByteArray());
            var hour = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600;
            string organization, project, database, usagePath;
            await using (var primary = await TerraBaseTestServer.StartAsync(Configure, issuer.Route))
            {
                using var dashboard = tls.CreateClient(primary.ClientAddress!, issuer.Mint(user));
                organization = await CreatedIdAsync(dashboard, "/v1/management/organizations", new { name = "Restarted" });
                project = await CreatedIdAsync(dashboard, $"/v1/management/organizations/{organization}/projects", new { name = "main" });
                database = await CreatedIdAsync(dashboard, $"/v1/management/organizations/{organization}/projects/{project}/databases", new { name = "orders" });
                usagePath = $"/v1/management/organizations/{organization}/projects/{project}/databases/{database}/usage";
                using var channel = peers.CreateChannel(primary.PeerAddress!);
                await new CatalogUsage.CatalogUsageClient(channel).AddAsync(Batch(epoch, 1, organization, project, database, hour, reads: 7));
                await primary.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);
            }

            // Its acknowledgement was lost: the follower sends the batch again to the restarted primary, then its next one.
            await using (var primary = await TerraBaseTestServer.StartAsync(Configure, issuer.Route))
            {
                using var dashboard = tls.CreateClient(primary.ClientAddress!, issuer.Mint(user));
                using var channel = peers.CreateChannel(primary.PeerAddress!);
                var client = new CatalogUsage.CatalogUsageClient(channel);
                await client.AddAsync(Batch(epoch, 1, organization, project, database, hour, reads: 7));
                await client.AddAsync(Batch(epoch, 2, organization, project, database, hour, reads: 2));

                var usage = await dashboard.GetFromJsonAsync<JsonElement>(Relative(usagePath));
                await Assert.That(usage.GetProperty("reads").GetInt64()).IsEqualTo(9);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_follower_refuses_usage_batches()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        await using var follower = await StartFollowerAsync(peers, tls, issuer, new Uri($"https://localhost:{TerraBaseTestServer.FreePort()}"));
        using var channel = peers.CreateChannel(follower.PeerAddress!);

        var notPrimary = await Assert.ThrowsAsync<RpcException>(async () => await new CatalogUsage.CatalogUsageClient(channel)
            .AddAsync(Batch(ByteString.CopyFrom(EntityIds.New().ToByteArray()), 1, "o", "p", "d", 0, reads: 1)));

        await Assert.That(notPrimary!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
    }

    private static UsageBatch Batch(
        ByteString epoch, ulong sequence, string organization, string project, string database, long hour, ulong reads, string sender = "test")
    {
        var batch = new UsageBatch { Protocol = Features.Management.Usage.UsageReceiver.Protocol, Epoch = epoch, Sequence = sequence, Sender = sender };
        batch.Counts.Add(new UsageCount { OrganizationId = organization, ProjectId = project, DatabaseId = database, Hour = hour, Reads = reads });
        return batch;
    }

    private static UsageBatch Scoped(UsageBatch batch, Features.Replication.V1.UsageScope scope)
    {
        batch.Counts[0].Scope = scope;
        return batch;
    }

    // Refuses the replication-lane reservations above the 4 KiB that a usage batch of totals alone takes, while Refuse is set.
    private sealed class MeasurementMemory(IMemoryBudget inner) : IMemoryBudget
    {
        public bool Refuse { get; set; }

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, QosContext.Current);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant) =>
            Refuse && lane == MemoryLane.Replication && bytes > 4096 ? null : inner.TryReserve(lane, bytes, tenant);
    }

    // Delivers every batch, but fails the first call as if its acknowledgement had been lost.
    private sealed class LostFirstAcknowledgement(CatalogPrimaryClient inner) : ICatalogPrimaryClient
    {
        public List<ulong> Sequences { get; } = [];

        public Task<bool> AddQueryShapesAsync(QueryShapesBatch batch, CancellationToken cancellationToken) =>
            inner.AddQueryShapesAsync(batch, cancellationToken);

        public async Task<AddUsageResult> AddUsageAsync(UsageBatch batch, CancellationToken cancellationToken)
        {
            var added = await inner.AddUsageAsync(batch, cancellationToken);
            lock (Sequences)
            {
                Sequences.Add(batch.Sequence);
                if (Sequences.Count == 1)
                {
                    throw new RpcException(new Status(StatusCode.Unavailable, "the acknowledgement was lost"));
                }
            }

            return added;
        }
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static Task<TerraBaseTestServer> StartFollowerAsync(
        PeerTls peers, ClientTls tls, TestIssuer issuer, Uri primary, Action<IServiceCollection>? services = null, string? region = null) =>
        TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            tls.Apply(settings);
            TestIssuer.Configure(settings);
            settings["catalog-primary"] = primary.ToString();
            if (region is not null)
            {
                settings["region"] = region;
            }
        }, collection =>
        {
            issuer.Route(collection);
            services?.Invoke(collection);
        });

    private static async Task ReadyAsync(TerraBaseTestServer server) =>
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var response = await server.Client.GetAsync(Relative("/readyz"), token);
            return response.StatusCode == HttpStatusCode.OK;
        });

    private static async Task<HttpStatusCode> PutDatabaseAsync(HttpClient data, string project, string name)
    {
        using var response = await data.PutAsync(Relative($"/v1/projects/{project}/databases/{name}"), null);
        return response.StatusCode;
    }

    private static async Task NotPrimaryAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await CodeAsync(response)).IsEqualTo("not_primary");
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString();

    private static async Task<string> CreatedIdAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(Relative(path), body);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<long> CountAsync(TerraBaseTestServer server, string collection)
    {
        var store = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        return await transaction.CountDocumentsAsync(collection, CancellationToken.None);
    }

    private static async Task WriteGroupAsync(IServerStreamWriter<FollowResponse> responses, ulong sequence, IReadOnlyList<DocumentChange> changes, byte[] digest)
    {
        await responses.WriteAsync(new FollowResponse { GroupBegin = new GroupBegin { Sequence = sequence, ChangeCount = (uint)changes.Count } });
        await responses.WriteAsync(new FollowResponse { Changes = CatalogProtocol.Page(changes) });
        await responses.WriteAsync(new FollowResponse { GroupEnd = new GroupEnd { Digest = ByteString.CopyFrom(digest) } });
    }

    // A request a follower of follower-region would forward; length -1 declares the body's own length.
    private static ForwardRequest Request(
        string method, string path, byte[]? body = null, uint protocol = ForwardingProtocol.Version, string query = "", string region = "follower-region", byte[]? envelope = null,
        int? length = -1)
    {
        var request = new ForwardRequest { Protocol = protocol, Method = method, Path = path, Query = query, Region = region };
        request.Headers.Add(new ForwardedHeader { Name = "Host", Value = "terrabase.test" });
        envelope ??= body is null ? null : ApplicationEnvelope.Encode(body).ToArray();
        if (envelope is not null)
        {
            request.Body = ByteString.CopyFrom(envelope);
            request.Headers.Add(new ForwardedHeader { Name = "Content-Type", Value = "application/json" });
        }

        length = length == -1 ? body?.Length : length;
        if (length is { } declared)
        {
            request.Headers.Add(new ForwardedHeader { Name = "Content-Length", Value = declared.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        return request;
    }

    private static ForwardRequest WithHeader(ForwardRequest request, string name, string value)
    {
        request.Headers.Add(new ForwardedHeader { Name = name, Value = value });
        return request;
    }

    // The head and the whole body of a Forward call.
    private static async Task<(ForwardHead Head, byte[] Body)> ForwardAsync(CatalogForwarding.CatalogForwardingClient client, ForwardRequest request)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        using var call = client.Forward(request, cancellationToken: deadline.Token);
        ForwardHead? head = null;
        using var body = new MemoryStream();
        while (await call.ResponseStream.MoveNext(deadline.Token))
        {
            if (call.ResponseStream.Current.Head is { } first)
            {
                head = first;
            }
            else
            {
                body.Write(ApplicationEnvelope.Decode(call.ResponseStream.Current.Body.Span, 1024 * 1024));
            }
        }

        return (head ?? throw new InvalidOperationException("the primary sent no head"), body.ToArray());
    }
}
