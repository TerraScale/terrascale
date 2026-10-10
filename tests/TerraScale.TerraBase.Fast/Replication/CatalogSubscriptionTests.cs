using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class CatalogSubscriptionTests
{
    [Test]
    public async Task Wrong_region_JWT_subscriptions_on_a_catalog_follower_do_not_create_a_local_database()
    {
        using var peers = new PeerTls();
        await using var primary = await ManagementApi.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["region"] = "sfo-1";
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var faker = new Faker();
        var organization = await primary.CreateOrganizationAsync(faker.Company.CompanyName());
        var project = await primary.CreateProjectAsync(organization, "main");
        await primary.VerifyProjectBindingAsync(organization, project);
        var database = new TenantContext(organization, PlanTier.Hobby, project, EntityIds.New().ToString());
        // The region's catalog primary records a database served elsewhere, as WAN discovery does: no local file.
        await Assert.That(await primary.Server.Services.GetRequiredService<ITenantDirectory>()
            .AdoptDatabaseAsync(database, "orders", new DatabasePlacement("gru-1", [], 1), deadline.Token) is Done).IsTrue();
        var credentials = Path.Combine(primary.Server.DataDirectory, "user.creds");
        await File.WriteAllTextAsync(credentials,
            "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n", deadline.Token);
        var network = new FakeRegionNetwork();
        var creations = 0;
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            primary.Tls.Apply(settings);
            TestIssuer.Configure(settings);
            settings["region"] = "sfo-1";
            settings["node-name"] = "subscription-follower";
            settings["catalog-primary"] = primary.Server.PeerAddress!.ToString();
            settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
            settings["nats-url"] = "nats://127.0.0.1:4222";
            settings["nats-credentials-file"] = credentials;
        }, services =>
        {
            primary.Issuer.Route(services);
            services.AddSingleton(network.Join("sfo"));
            services.AddSingleton<IMemberStore>(new FakeMemberStore(TimeProvider.System));
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, tenant) =>
            {
                if (step == "create.published" && tenant.DatabaseId == database.DatabaseId)
                {
                    Interlocked.Increment(ref creations);
                }
            });
        });
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var ready = await follower.Client.GetAsync(new Uri("/readyz", UriKind.Relative), token);
            return ready.StatusCode == HttpStatusCode.OK;
        });
        var engine = follower.Services.GetRequiredService<IStorageEngine>();
        await Assert.That(await engine.OpenDatabaseAsync(database, deadline.Token)).IsNull();

        var route = new Uri($"/v1/projects/{project}/databases/orders/collections/items/subscribe", UriKind.Relative);
        foreach (var (grantProject, grantDatabase, scope, expired, status, code) in new[]
        {
            (project, "orders", "shapes:read", false, HttpStatusCode.MisdirectedRequest, "wrong_region"),
            (EntityIds.New().ToString(), "orders", "shapes:read", false, HttpStatusCode.NotFound, "unknown_database"),
            (project, "other", "shapes:read", false, HttpStatusCode.NotFound, "unknown_database"),
            (project, "orders", "documents:read", false, HttpStatusCode.Forbidden, "forbidden_scope"),
            (project, "orders", "shapes:read", true, HttpStatusCode.Unauthorized, "unauthorized"),
        })
        {
            // The subject is not a catalog member; only the signed project/database grants authorize this route.
            using var client = primary.Tls.CreateClient(follower.ClientAddress!, primary.Issuer.Mint(EntityIds.New().ToString(), token =>
            {
                token.Claims["project_id"] = grantProject;
                token.Claims["database"] = grantDatabase;
                token.Claims["scope"] = scope;
                if (expired)
                {
                    token.IssuedAt = DateTime.UtcNow.AddMinutes(-1);
                    token.Expires = DateTime.UtcNow.AddSeconds(-10);
                }
            }));
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await Assert.That(response.StatusCode).IsEqualTo(status);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
            await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
            await Assert.That(response.Headers.Contains("X-TerraBase-Database-Home")).IsEqualTo(status == HttpStatusCode.MisdirectedRequest);
            await Assert.That(response.Headers.Contains("X-TerraBase-Database-Regions")).IsEqualTo(status == HttpStatusCode.MisdirectedRequest);
            if (status == HttpStatusCode.MisdirectedRequest)
            {
                await Assert.That(response.Headers.GetValues("X-TerraBase-Database-Home").Single()).IsEqualTo("gru-1");
                await Assert.That(response.Headers.GetValues("X-TerraBase-Database-Regions").Single()).IsEqualTo("gru-1");
            }
        }

        await Assert.That(Volatile.Read(ref creations)).IsEqualTo(0);
        await Assert.That(await engine.OpenDatabaseAsync(database, deadline.Token)).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_unreadable_subscription_catalog_preserves_the_database_and_retry_recovers(bool afterStart)
    {
        using var peers = new PeerTls();
        await using var primary = await ManagementApi.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["region"] = "sfo-1";
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var database = await primary.CreateManagedDatabaseAsync("db" + new Faker().Random.AlphaNumeric(8).ToLowerInvariant());
        await primary.VerifyProjectBindingAsync(database.Organization, database.Project);
        var credentials = Path.Combine(primary.Server.DataDirectory, "subscription.creds");
        await File.WriteAllTextAsync(credentials,
            "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n", deadline.Token);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var network = new FakeRegionNetwork();
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            primary.Tls.Apply(settings);
            TestIssuer.Configure(settings);
            settings["region"] = "sfo-1";
            settings["node-name"] = "subscription-follower";
            settings["catalog-primary"] = primary.Server.PeerAddress!.ToString();
            settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
            settings["nats-url"] = "nats://127.0.0.1:4222";
            settings["nats-credentials-file"] = credentials;
        }, services =>
        {
            primary.Issuer.Route(services);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(network.Join("sfo"));
            services.AddSingleton<IMemberStore>(new FakeMemberStore(clock));
        });
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var ready = await follower.Client.GetAsync(new Uri("/readyz", UriKind.Relative), token);
            return ready.StatusCode == HttpStatusCode.OK;
        });
        using var client = primary.Tls.CreateClient(follower.ClientAddress!, primary.Issuer.Mint(primary.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = $"{ApiKeyScopes.ShapesRead} {ApiKeyScopes.DocumentsWrite} {ApiKeyScopes.DocumentsRead}";
            token.Expires = clock.GetUtcNow().AddMinutes(5).UtcDateTime;
        }));
        await ManagementApi.CreateCollectionAsync(client, database, "items");
        var tenants = follower.Services.GetRequiredService<ITenantDirectory>();
        var found = await tenants.FindDatabaseAsync(database.Organization, database.Project, database.Name, deadline.Token);
        var tenant = found is TenantContext resolved ? resolved : throw new InvalidOperationException("the seeded database must resolve");
        var engine = follower.Services.GetRequiredService<IStorageEngine>();
        var incarnation = (await engine.OpenDatabaseAsync(tenant, deadline.Token))!.Incarnation;
        var file = Path.Combine(follower.DataDirectory, database.Organization, database.Project, database.Id + ".db");
        var primaryStore = await primary.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(deadline.Token);
        byte[] version;
        await using (var transaction = await primaryStore.BeginTransactionAsync(write: false, deadline.Token))
        {
            version = (await transaction.GetAsync("catalog", "version", deadline.Token))!.Json.ToArray();
        }

        var meter = follower.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long active = 0;
        long reserved = 0;
        var liveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name is
                "terrabase.subscriptions.active" or "terrabase.memory.reserved" or "terrabase.shape.live.waits")
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "terrabase.subscriptions.active")
            {
                Interlocked.Add(ref active, value);
            }
            else if (instrument.Name == "terrabase.memory.reserved")
            {
                Interlocked.Exchange(ref reserved, value);
            }
            else if (value == 1)
            {
                liveEntered.TrySetResult();
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();
        var baseline = Volatile.Read(ref reserved);
        var releasedCeiling = baseline;
        var route = new Uri($"/v1/projects/{database.Project}/databases/{database.Name}/collections/items/subscribe", UriKind.Relative);
        using var response = afterStart ? await client.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, deadline.Token) : null;
        using var reader = response is null ? null : new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        if (afterStart)
        {
            await Assert.That(response!.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/event-stream");
            await ReadSnapshotAsync(reader!, deadline.Token);
            await liveEntered.Task.WaitAsync(deadline.Token);
            listener.RecordObservableInstruments();
            await Assert.That(Volatile.Read(ref reserved) - baseline).IsGreaterThanOrEqualTo(32 * 1024 * 1024);
            releasedCeiling = Volatile.Read(ref reserved) - (32 * 1024 * 1024);
        }

        await WriteVersionAsync("\"99\""u8.ToArray());
        await TerraBaseTestServer.EventuallyAsync(async token => await tenants.CheckCatalogAsync(token) is Unavailable);
        if (afterStart)
        {
            clock.Advance(TimeSpan.FromSeconds(21));
            await Assert.That(await reader!.ReadLineAsync(deadline.Token)).IsEqualTo("event: error");
            var body = await reader.ReadLineAsync(deadline.Token);
            await Assert.That(body).StartsWith("data: ");
            using var error = JsonDocument.Parse(body![6..]);
            await Assert.That(error.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unavailable");
            await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsEqualTo("");
            await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsNull();
        }
        else
        {
            await ErrorAssert.ErrorAsync(client.GetAsync(route, deadline.Token), HttpStatusCode.ServiceUnavailable, "unavailable",
                "the replicated management catalog is not at a version this build supports");
        }

        await TerraBaseTestServer.EventuallyAsync(_ =>
        {
            listener.RecordObservableInstruments();
            return Task.FromResult(Volatile.Read(ref active) == 0 && Volatile.Read(ref reserved) <= releasedCeiling);
        });
        await Assert.That(File.Exists(file)).IsTrue();
        await Assert.That((await engine.OpenDatabaseAsync(tenant, deadline.Token))!.Incarnation).IsEqualTo(incarnation);
        await WriteVersionAsync(version);
        await TerraBaseTestServer.EventuallyAsync(async token => await tenants.CheckCatalogAsync(token) is Done);
        using var retried = await client.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var retryReader = new StreamReader(await retried.Content.ReadAsStreamAsync(deadline.Token));
        await ReadSnapshotAsync(retryReader, deadline.Token);

        async Task WriteVersionAsync(byte[] value)
        {
            await using var transaction = await primaryStore.BeginTransactionAsync(write: true, deadline.Token);
            await transaction.PutAsync("catalog", "version", value, deadline.Token);
            await transaction.CommitAsync(deadline.Token);
        }
    }

    private static async Task ReadSnapshotAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var upToDate = false;
        while (!upToDate)
        {
            await Assert.That(await reader.ReadLineAsync(cancellationToken)).IsEqualTo("event: shape");
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    using var page = JsonDocument.Parse(line[6..]);
                    upToDate = page.RootElement.GetProperty("up_to_date").GetBoolean();
                }
            }
        }
    }
}
