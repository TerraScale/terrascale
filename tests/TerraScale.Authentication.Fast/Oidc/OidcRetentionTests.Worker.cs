using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Fast.Oidc;

public sealed partial class OidcRetentionTests
{
    [Test]
    public async Task Scheduled_worker_prunes_real_store_records_only_after_its_interval_and_reports_the_pass()
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var clock = new RetentionClock();
        var store = new AuthStore(database, clock);
        var token = Token("worker-tenant", clock.GetUtcNow() - OidcRetention.ProofLifetime);
        token.Type = TokenTypeIdentifiers.AccessToken;
        await SeedAsync(store, token);
        var grant = new OidcAuthorization
        {
            Id = AuthStore.NewId(),
            InstanceId = token.InstanceId,
            CreationDate = token.CreationDate,
            Type = AuthorizationTypes.AdHoc,
        };
        await store.CreateAsync(AuthStore.OidcAuthorizationCollection, grant.InstanceId!, grant.Id!,
            JsonSerializer.SerializeToElement(grant, OidcJson.Default.OidcAuthorization));
        var log = new RetentionPass();
        using var worker = new OidcPruner(new OidcRetention(store, database, clock), clock, log);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await clock.Registered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
            await Assert.That(log.Completed.Task.IsCompleted).IsFalse();
            await Assert.That(await store.GetAsync(AuthStore.OidcTokenCollection, token.InstanceId!, token.Id!)).IsNotNull();
            clock.Advance(TimeSpan.FromTicks(1));
            await Assert.That(await log.Completed.Task.WaitAsync(TimeSpan.FromSeconds(15)))
                .IsEqualTo("OIDC retention pruned 1 token records and 1 authorization records");
            await Assert.That(await store.GetAsync(AuthStore.OidcTokenCollection, token.InstanceId!, token.Id!)).IsNull();
            await Assert.That(await store.GetAsync(AuthStore.OidcAuthorizationCollection, grant.InstanceId!, grant.Id!)).IsNull();
            await Assert.That(await store.FindUniqueAsync(token.InstanceId!, "oidc_token_reference", token.ReferenceId!)).IsNull();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private sealed class RetentionClock : FakeTimeProvider
    {
        public TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            this.Registered.TrySetResult();
            return timer;
        }
    }

    private sealed class RetentionPass : ILogger<OidcPruner>
    {
        public TaskCompletionSource<string> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                this.Completed.TrySetException(exception);
            }
            else if (logLevel == LogLevel.Information)
            {
                this.Completed.TrySetResult(formatter(state, exception));
            }
        }
    }
}
