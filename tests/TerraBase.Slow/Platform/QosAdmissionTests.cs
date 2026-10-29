using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Management;
using TerraBase.Fast.Storage;
using TerraBase.Features.Documents;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class QosAdmissionTests
{
    [Test]
    public async Task Project_JWTs_share_the_organization_concurrency_lane_with_document_keys()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        _ = await tenant.ContextAsync(api.Server, "app");
        using var owner = api.As(tenant.User);
        using var jwt = api.WithToken(api.Issuer.Mint(tenant.User, token =>
        {
            token.Claims["project_id"] = tenant.ProjectId;
            token.Claims["scope"] = "documents:read databases:read";
        }));
        var admission = api.Server.Services.GetRequiredService<IQosAdmission>();
        var lanes = new List<IDisposable>();
        try
        {
            for (var i = 0; i < QosPolicy.For(PlanTier.Hobby).Concurrency; i++)
            {
                lanes.Add(admission.TryEnter(new TenantContext(tenant.OrganizationId, PlanTier.Hobby), out _)!);
            }

            using var refused = await jwt.GetAsync(tenant.Database("app", "/collections"));
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(refused.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
            using var keyRefused = await tenant.Data.GetAsync(tenant.Database("app", "/collections"));
            await Assert.That(keyRefused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        }
        finally
        {
            foreach (var lane in lanes)
            {
                lane.Dispose();
            }
        }

        using var recovered = await jwt.GetAsync(tenant.Database("app", "/collections"));
        recovered.EnsureSuccessStatusCode();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Process_memory_refusal_precedes_cold_plan_or_membership_materialization(bool management)
    {
        const long Budget = 4 * 1024 * 1024;
        var hook = new UsageCommitHook();
        await using var api = await RequestLogMemoryFixture.StartAsync(settings => settings["memory-budget"] = "4194304", services =>
        {
            hook.Install(services);
            services.AddHttpContextAccessor();
        });
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        _ = await tenant.ContextAsync(api.Server, "app");
        // Any catalog commit invalidates the warmed plan cache.
        _ = await api.CreateOrganizationAsync("Invalidate plan cache");
        api.Server.Services.GetRequiredService<DocumentCache>().Dispose();
        using var owner = api.As(tenant.User);
        var client = management ? owner : tenant.Data;
        var uri = management ? new Uri($"/v1/management/organizations/{tenant.OrganizationId}", UriKind.Relative)
            : tenant.Database("app", "/collections");
        await RequestLogMemoryFixture.DrainAsync(api.Server);
        var reads = 0;
        var requests = api.Server.Services.GetRequiredService<IHttpContextAccessor>();
        // Background catalog convergence must not count as request materialization.
        hook.BeforeGet = _ =>
        {
            if (requests.HttpContext?.Request.Path.Value == uri.OriginalString)
            {
                Interlocked.Increment(ref reads);
            }
        };
        using (var all = MemoryAccounting.ReserveAvailable(api.Server, MemoryLane.Foreground, Budget))
        {
            await Assert.That(all).IsNotNull();
            using var refused = await client.GetAsync(uri);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString())
                .IsEqualTo("tenant_memory");
            await Assert.That(Volatile.Read(ref reads)).IsEqualTo(0);
        }

        using var recovered = await client.GetAsync(uri);
        recovered.EnsureSuccessStatusCode();
        await Assert.That(Volatile.Read(ref reads)).IsGreaterThan(0);
    }

    [Test]
    public async Task Management_concurrency_is_held_until_response_serialization_finishes()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var barrier = new CommitResponseBarrier();
        await using var api = await ManagementApi.StartAsync(services: services => services.AddSingleton<IStartupFilter>(barrier));
        var organization = await api.CreateOrganizationAsync("Commit concurrency");
        var pending = new List<Task<HttpResponseMessage>>();
        try
        {
            for (var i = 0; i < QosPolicy.For(PlanTier.Hobby).Concurrency; i++)
            {
                pending.Add(SendAsync(hold: true));
            }

            await barrier.AllStarted.Task.WaitAsync(deadline.Token);
            using (var refused = await SendAsync(hold: false))
            {
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(refused.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
                await Assert.That((await refused.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("error").GetProperty("code").GetString())
                    .IsEqualTo("tenant_busy");
            }

            barrier.Release.TrySetResult();
            foreach (var response in await Task.WhenAll(pending).WaitAsync(deadline.Token))
            {
                using (response)
                {
                    response.EnsureSuccessStatusCode();
                }
            }

            await barrier.AllCompleted.Task.WaitAsync(deadline.Token);
            using var recovered = await SendAsync(hold: false);
            recovered.EnsureSuccessStatusCode();
        }
        finally
        {
            barrier.Release.TrySetResult();
            await deadline.CancelAsync();
            foreach (var task in pending)
            {
                try
                {
                    (await task).Dispose();
                }
                catch (OperationCanceledException)
                {
                    // Join every listener request before disposing its server.
                }
            }
        }

        async Task<HttpResponseMessage> SendAsync(bool hold)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/v1/management/organizations/{organization}", UriKind.Relative));
            if (hold)
            {
                request.Headers.Add("X-Test-Hold-Response", "true");
            }

            return await api.Client.SendAsync(request, deadline.Token);
        }
    }

    [Test]
    public async Task Leaving_an_unlimited_plan_starts_a_finite_bucket_without_lockout()
    {
        await using var api = await ManagementApi.StartAsync(time: new AdmissionClock());
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        _ = await tenant.ContextAsync(api.Server, "app");
        using var owner = api.Service(tenant.User);
        var plan = new Uri($"/v1/management/organizations/{tenant.OrganizationId}/plan", UriKind.Relative);
        using (var enterprise = await owner.PutAsJsonAsync(plan, new { plan = "enterprise" }))
        {
            enterprise.EnsureSuccessStatusCode();
        }
        using (var unlimited = await tenant.Data.GetAsync(tenant.Database("app", "/collections")))
        {
            unlimited.EnsureSuccessStatusCode();
        }
        using (var hobby = await owner.PutAsJsonAsync(plan, new { plan = "hobby" }))
        {
            hobby.EnsureSuccessStatusCode();
        }
        using var admitted = await tenant.Data.GetAsync(tenant.Database("app", "/collections"));
        admitted.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task Oversized_management_body_is_refused_before_request_binding()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Body limit");
        using var body = new StringContent($"{{\"name\":\"{new Faker().Random.String2(4 * 1024 * 1024, "abcdef")}\"}}", Encoding.UTF8, "application/json");
        using var response = await api.Client.PostAsync(new Uri($"/v1/management/organizations/{organization}/projects", UriKind.Relative), body);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        using var recovered = await api.Client.GetAsync(new Uri($"/v1/management/organizations/{organization}", UriKind.Relative));
        recovered.EnsureSuccessStatusCode();
        await Assert.That((await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()).IsEqualTo("Body limit");
    }

    [Test]
    public async Task Chunked_body_memory_refusal_releases_admission_and_does_not_write()
    {
        await using var api = await RequestLogMemoryFixture.StartAsync(settings => settings["memory-budget"] = "4194304");
        using var hobby = await api.CreateTenantAsync("documents:read", "documents:write");
        var database = await hobby.ContextAsync(api.Server, "app");
        await StorageFixture.CreateCollectionAsync(hobby, "app", "items");
        var uri = hobby.Database("app", $"/collections/items/documents/by-key?id={StorageFixture.Id("chunked")}");
        using (var refused = await hobby.Data.PutAsJsonAsync(uri, new { padding = new Faker().Random.String2(100_000, "abcdef") }))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(refused.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
            await Assert.That((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString())
                .IsEqualTo("tenant_memory");
        }

        using (var missing = await hobby.Data.GetAsync(uri))
        {
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        await RequestLogMemoryFixture.DrainAsync(api.Server);
        using (var released = MemoryAccounting.ReserveAvailable(api.Server, MemoryLane.Foreground, 262_144, database))
        {
            await Assert.That(released).IsNotNull();
        }

        using var recovered = await hobby.Data.PutAsJsonAsync(uri, new { padding = "small chunked body" });
        recovered.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task Hobby_concurrency_is_shared_across_keys_and_released_after_rejection()
    {
        await using var api = await ManagementApi.StartAsync();
        using var hobby = await api.CreateTenantAsync("documents:read", "documents:write");
        using var other = await api.CreateTenantAsync("documents:read", "documents:write");
        var database = await hobby.ContextAsync(api.Server, "app");
        _ = await other.ContextAsync(api.Server, "app");
        using var secondKey = api.DataClient(await api.Server.IssueNarrowedAsync(hobby.OrganizationId, hobby.ProjectId, null, "documents:read"));
        var admission = api.Server.Services.GetRequiredService<IQosAdmission>();
        var held = new List<IDisposable>();
        try
        {
            for (var i = 0; i < QosPolicy.For(PlanTier.Hobby).Concurrency; i++)
            {
                held.Add(admission.TryEnter(database, out _) ?? throw new InvalidOperationException("fixture admission failed"));
            }

            using var first = await hobby.Data.GetAsync(hobby.Database("app", "/collections"));
            using var second = await secondKey.GetAsync(hobby.Database("app", "/collections"));
            using var unaffected = await other.Data.GetAsync(other.Database("app", "/collections"));
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(first.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
            await Assert.That((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("tenant_busy");
            await Assert.That(unaffected.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var metrics = await api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
            await Assert.That(metrics).Contains("tenant_bucket=");
            await Assert.That(metrics).DoesNotContain(hobby.OrganizationId);
            await Assert.That(metrics).DoesNotContain(other.OrganizationId);
        }
        finally
        {
            foreach (var lease in held)
            {
                lease.Dispose();
            }
        }

        using var recovered = await hobby.Data.GetAsync(hobby.Database("app", "/collections"));
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Rate_tokens_replenish_without_resetting_the_organization_bucket()
    {
        var clock = new AdmissionClock();
        await using var api = await ManagementApi.StartAsync(time: clock);
        using var hobby = await api.CreateTenantAsync("documents:read", "documents:write");
        _ = await hobby.ContextAsync(api.Server, "app");
        HttpStatusCode status = HttpStatusCode.OK;
        for (var i = 0; i <= QosPolicy.For(PlanTier.Hobby).Burst && status == HttpStatusCode.OK; i++)
        {
            using var response = await hobby.Data.GetAsync(hobby.Database("app", "/collections"));
            status = response.StatusCode;
        }

        await Assert.That(status).IsEqualTo(HttpStatusCode.TooManyRequests);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var recovered = await hobby.Data.GetAsync(hobby.Database("app", "/collections"));
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private sealed class CommitResponseBarrier : IStartupFilter
    {
        private int _started;
        private int _completed;
        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Headers.ContainsKey("X-Test-Hold-Response"))
                {
                    // Registered before endpoint leases: this completion runs after their disposal.
                    context.Response.OnCompleted(() =>
                    {
                        if (Interlocked.Increment(ref _completed) == QosPolicy.For(PlanTier.Hobby).Concurrency)
                        {
                            AllCompleted.TrySetResult();
                        }

                        return Task.CompletedTask;
                    });
                    context.Response.OnStarting(async () =>
                    {
                        if (Interlocked.Increment(ref _started) == QosPolicy.For(PlanTier.Hobby).Concurrency)
                        {
                            AllStarted.TrySetResult();
                        }

                        await Release.Task.WaitAsync(context.RequestAborted);
                    });
                }

                await continuation(context);
            });
            next(app);
        };
    }

    private sealed class AdmissionClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
