using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Slow.Management;

/// <summary><c>DELETE /v1/terrascale/account</c>: a signed-in user deletes its own account, over real HTTP on the real TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AccountDeletionTests(TerraBaseFixture fixture)
{
    private const string Account = "/v1/terrascale/account";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Account_deletion_rereads_a_concurrent_deferral_or_worker_completion_before_committing(bool workerCompletes)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        using var interleaving = new InterleavedAccountCommit { InnerHandler = TerraBaseFixture.NewHandler() };
        using var transport = new HttpClient(interleaving);
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, clock: clock, services: services => services.AddSingleton(transport));
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token, "Solo");
        var services = oidc.Server.App.Services;
        var store = services.GetRequiredService<AuthStore>();
        var directory = services.GetRequiredService<PlatformDirectory>();
        var (intent, _) = ClosureJob.New("terrascale", "account", alice.Id, null, alice.Id, clock);
        // The timer is off; the explicit pass below makes the intent due at the controlled interleaving.
        await store.CreateAsync(AuthStore.ClosureJobCollection, "terrascale", ClosureJob.Key("account", alice.Id),
            JsonSerializer.SerializeToElement(intent with { NextAttemptAt = clock.UtcNow.AddHours(1) }, ClosureJson.Default.ClosureJob));
        interleaving.BeforeCommit = async cancellationToken =>
        {
            var current = (await directory.ClosureAsync("account", alice.Id, cancellationToken))!;
            if (workerCompletes)
            {
                clock.UtcNow = clock.UtcNow.AddHours(2);
                await services.GetRequiredService<ClosureWorker>().RunOnceAsync(cancellationToken);
                await Assert.That(await directory.ClosureAsync("account", alice.Id, cancellationToken)).IsNull();
            }
            else
            {
                await services.GetRequiredService<Closures>().DeferAsync(current, "project_closure_pending", cancellationToken);
                var deferred = (await directory.ClosureAsync("account", alice.Id, cancellationToken))!;
                await Assert.That(deferred.Version).IsNotEqualTo(current.Version);
                await Assert.That(deferred.Value.CommittedAt).IsNull();
            }
        };

        // The real commit transaction now carries a stale version. No transport response is replaced or fabricated.
        await oidc.OkAsync(HttpMethod.Delete, Account, token, status: HttpStatusCode.NoContent);
        await Assert.That(interleaving.Interleaved).IsTrue();
        await Assert.That(await services.GetRequiredService<Users>().GetAsync("terrascale", alice.Id)).IsNull();
        await Assert.That(await directory.OrganizationAsync(organization, CancellationToken.None)).IsNull();
        await Assert.That(await directory.ClosureAsync("account", alice.Id, CancellationToken.None)).IsNull();
        var proof = (await store.GetAsync(AuthStore.DeletionJobCollection, "terrascale", DeletionJob.Key("terrascale", "account", alice.Id)))!
            .Document.Deserialize(DeletionJson.Default.DeletionJob)!;
        await Assert.That(proof.ClosureGeneration).IsEqualTo(intent.Generation);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, token)).IsEqualTo((401, "unauthorized"));
        await Assert.That(await services.GetRequiredService<Users>().FindByEmailAsync("terrascale", alice.Email)).IsNull();
    }

    private sealed class InterleavedAccountCommit : DelegatingHandler
    {
        private int interleaved;

        public Func<CancellationToken, Task>? BeforeCommit { get; set; }

        public bool Interleaved => Volatile.Read(ref this.interleaved) != 0;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (this.BeforeCommit is { } beforeCommit && request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (body.RootElement.GetProperty("ops").EnumerateArray().Any(operation => operation.GetProperty("collection").GetString() == AuthStore.ClosureCommitCollection)
                    && Interlocked.CompareExchange(ref this.interleaved, 1, 0) == 0)
                {
                    await beforeCommit(cancellationToken);
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
