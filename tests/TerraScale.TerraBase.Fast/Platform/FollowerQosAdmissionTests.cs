using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Management;
using TerraBase.Features.Management;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class FollowerQosAdmissionTests
{
    [Test]
    public async Task A_lagging_follower_admits_the_known_organization_and_forwards_management_reads_of_new_databases()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var peers = new PeerTls();
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(peers.Apply, hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var catalog = new HeldCatalog($"projects/{database.Project}/databases");
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            api.Tls.Apply(settings);
            TestIssuer.Configure(settings);
            settings["catalog-primary"] = api.Server.PeerAddress!.ToString();
        }, services =>
        {
            api.Issuer.Route(services);
            services.AddSingleton<ICatalogApplyObserver>(catalog);
        });
        try
        {
            await catalog.CaughtUp.Task.WaitAsync(deadline.Token);
            await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "hold");
            await catalog.Paused.Task.WaitAsync(deadline.Token);
            var created = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "late");
            var tenants = follower.Services.GetRequiredService<ITenantDirectory>();
            await Assert.That(await tenants.FindDatabaseAsync(database.Organization, database.Project, "late", deadline.Token)).IsNull();
            using var client = api.Tls.CreateClient(follower.ClientAddress!, api.Issuer.Mint(api.User, token =>
            {
                token.Claims["project_id"] = database.Project;
                token.Claims["scope"] = "databases:read";
            }));
            hook.AfterGet = (collection, id, _) =>
            {
                if (ProjectAccess.Current?.ProjectId == database.Project
                    && collection == $"projects/{database.Project}/databases" && id == created.Id)
                {
                    read.TrySetResult();
                }

                return ValueTask.CompletedTask;
            };
            var pending = client.GetAsync(Route(database.Project, "late"), deadline.Token);
            try
            {
                // The primary receives the read while the follower still lacks the database record.
                await read.Task.WaitAsync(deadline.Token);
                await Assert.That(await tenants.FindDatabaseAsync(database.Organization, database.Project, "late", deadline.Token)).IsNull();
                catalog.Release.TrySetResult();
                using var response = await pending;
                response.EnsureSuccessStatusCode();
                await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
                    .GetProperty("id").GetString()).IsEqualTo(created.Id);
            }
            finally
            {
                catalog.Release.TrySetResult();
                await deadline.CancelAsync();
                try
                {
                    (await pending).Dispose();
                }
                catch (OperationCanceledException)
                {
                    // Join the forwarded listener request before disposing its servers.
                }
            }
        }
        finally
        {
            catalog.Release.TrySetResult();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Slow_forwarded_uploads_share_the_organization_lane_and_leave_paid_requests_admitted(bool chunked)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var peers = new PeerTls();
        await using var api = await ManagementApi.StartAsync(peers.Apply, managementCredential: true);
        var hobby = await api.CreateManagedDatabaseAsync();
        var paid = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(hobby.Organization, hobby.Project);
        await api.VerifyProjectBindingAsync(paid.Organization, paid.Project);
        await api.SetPlanAsync(api.User, paid.Organization, "pro", deadline.Token);
        var uploads = new UploadBarrier();
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            api.Tls.Apply(settings);
            TestIssuer.Configure(settings);
            settings["catalog-primary"] = api.Server.PeerAddress!.ToString();
            settings["memory-budget"] = "268435456";
        }, services =>
        {
            api.Issuer.Route(services);
            services.AddSingleton<IStartupFilter>(uploads);
        });
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await follower.Services.GetRequiredService<ITenantDirectory>().PlanAsync(paid.Organization, token) == PlanTier.Pro);
        using var hobbyClient = Client(hobby.Project);
        using var paidClient = Client(paid.Project);
        var pending = Enumerable.Range(0, QosPolicy.For(PlanTier.Hobby).Concurrency)
            .Select(_ => SendAsync(hobbyClient, hobby.Project, hold: true)).ToArray();
        try
        {
            await uploads.AllEntered.Task.WaitAsync(deadline.Token);
            using (var refused = await SendAsync(hobbyClient, hobby.Project, hold: true))
            {
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(refused.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
                var body = await refused.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
                await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("tenant_busy");
            }

            await Assert.That(uploads.EnteredCount()).IsEqualTo(QosPolicy.For(PlanTier.Hobby).Concurrency);
            using var allowed = await SendAsync(paidClient, paid.Project, hold: false);
            allowed.EnsureSuccessStatusCode();
            // Organization lookup must leave the original named route intact for the primary.
            await Assert.That((await allowed.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
                .GetProperty("id").GetString()).IsEqualTo(paid.Id);
            uploads.Release.TrySetResult();
            foreach (var response in await Task.WhenAll(pending).WaitAsync(deadline.Token))
            {
                using (response)
                {
                    response.EnsureSuccessStatusCode();
                }
            }

            await uploads.AllCompleted.Task.WaitAsync(deadline.Token);
            using var recovered = await SendAsync(hobbyClient, hobby.Project, hold: false);
            recovered.EnsureSuccessStatusCode();
        }
        finally
        {
            uploads.Release.TrySetResult();
            await deadline.CancelAsync();
            foreach (var task in pending)
            {
                try
                {
                    (await task).Dispose();
                }
                catch (OperationCanceledException)
                {
                    // Join cancelled listener requests before disposing either server.
                }
            }
        }

        HttpClient Client(string project)
        {
            return api.Tls.CreateClient(follower.ClientAddress!, api.Issuer.Mint(api.User, token =>
            {
                token.Claims["project_id"] = project;
                token.Claims["scope"] = "databases:read databases:write";
            }));
        }

        async Task<HttpResponseMessage> SendAsync(HttpClient client, string project, bool hold)
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, Route(project, "main"))
            {
                Content = chunked ? JsonContent.Create(new { replicaRegions = Array.Empty<string>() })
                    : new StringContent("{\"replicaRegions\":[]}", Encoding.UTF8, "application/json"),
            };
            if (hold)
            {
                request.Headers.Add("X-Test-Hold-Upload", "true");
            }

            return await client.SendAsync(request, deadline.Token);
        }
    }

    private static Uri Route(string project, string database) => new(ProjectApiRoute.Database
        .Replace("{project}", project, StringComparison.Ordinal)
        .Replace("{database}", database, StringComparison.Ordinal), UriKind.Relative);

    private sealed class HeldCatalog(string collection) : ICatalogApplyObserver
    {
        public string Collection => collection;
        public TaskCompletionSource CaughtUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask AppliedAsync(IReadOnlyList<DocumentChange> documents, CancellationToken cancellationToken)
        {
            foreach (var change in documents)
            {
                if (change.Json is not { } json)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(json);
                if (document.RootElement.GetProperty("name").GetString() == "hold")
                {
                    Paused.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }
            }
        }

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask CaughtUpAsync(CancellationToken cancellationToken)
        {
            CaughtUp.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UploadBarrier : IStartupFilter
    {
        private int _entered;
        private int _completed;
        public int EnteredCount() => Volatile.Read(ref _entered);
        public TaskCompletionSource AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                var original = context.Request.Body;
                using var blocked = context.Request.Headers.ContainsKey("X-Test-Hold-Upload")
                    ? new HeldRead(original, this) : null;
                if (blocked is not null)
                {
                    context.Request.Body = blocked;
                    context.Response.OnCompleted(() =>
                    {
                        if (Interlocked.Increment(ref _completed) == QosPolicy.For(PlanTier.Hobby).Concurrency + 1)
                        {
                            // Includes the rejected fifth request; all admitted leases have now returned.
                            AllCompleted.TrySetResult();
                        }

                        return Task.CompletedTask;
                    });
                }

                try
                {
                    await continuation(context);
                }
                finally
                {
                    context.Request.Body = original;
                }
            });
            next(app);
        };

        private sealed class HeldRead(Stream inner, UploadBarrier owner) : Stream
        {
            private int _started;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _started, 1) == 0)
                {
                    if (Interlocked.Increment(ref owner._entered) == QosPolicy.For(PlanTier.Hobby).Concurrency)
                    {
                        owner.AllEntered.TrySetResult();
                    }

                    await owner.Release.Task.WaitAsync(cancellationToken);
                }

                return await inner.ReadAsync(buffer, cancellationToken);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
