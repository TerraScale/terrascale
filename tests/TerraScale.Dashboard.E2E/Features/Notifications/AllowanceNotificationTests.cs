using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Identity;
using TerraScale.Dashboard.Shared.Accounts;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Notifications;

public sealed class AllowanceNotificationTests
{
    private const string OrganizationId = "01JA0000000000000000000001";
    private const string UserId = "01JA0000000000000000000002";
    private const string AccountId = "01JA0000000000000000000003";

    [Test]
    public async Task EightyOnePercentCreatesOneDurableAlertAndOwnerInboxAcrossRestarts()
    {
        var state = await StateAsync();
        var clock = new Clock();
        var backend = new Backend(clock) { Operations = PlanCatalog.Limits("free").MonthlyOperations * 81 / 100 };
        await Service(state, backend, clock).RunOnceAsync(default);
        await Service(state, backend, clock).RunOnceAsync(default);
        var context = await ContextAsync(state, "owner");
        await AllowanceNotificationInbox.HydrateAsync(state, context, default);
        await AllowanceNotificationInbox.HydrateAsync(state, context, default);

        await Assert.That(backend.Sent.Count).IsEqualTo(1);
        await Assert.That(context.Account.Notifications.Count).IsEqualTo(1);
        var notification = context.Account.Notifications.Single();
        await Assert.That(notification.Title).Contains("80%");
        await Assert.That(notification.Type).IsEqualTo("allowance-operations");
        await Assert.That(notification.VisibleTo(context)).IsTrue();
        context.Organization!.Members[UserId].Role = "member";
        await Assert.That(notification.VisibleTo(context)).IsFalse();
    }

    [Test]
    public async Task BothAllowancesCrossBothThresholdsOnceAndResetNextUtcMonth()
    {
        var state = await StateAsync();
        var clock = new Clock();
        var limits = PlanCatalog.Limits("free");
        var backend = new Backend(clock) { Operations = limits.MonthlyOperations, Bytes = limits.StorageBytes };
        var service = Service(state, backend, clock);
        await service.RunOnceAsync(default);
        await service.RunOnceAsync(default);
        await Assert.That(backend.Sent.Count).IsEqualTo(4);
        clock.Now = clock.Now.AddMonths(1);
        await service.RunOnceAsync(default);
        await Assert.That(backend.Sent.Count).IsEqualTo(8);
        var ledger = await state.GetAsync(AllowanceNotifications.Collection, OrganizationId, default);
        await Assert.That(AllowanceNotifications.Read(ledger!, OrganizationId).Alerts.Count).IsEqualTo(4);
    }

    [Test]
    public async Task UnknownDeliveryIsNotResentAndKnownRefusalCanRecover()
    {
        var state = await StateAsync();
        var clock = new Clock();
        var limits = PlanCatalog.Limits("free");
        var backend = new Backend(clock) { Operations = limits.MonthlyOperations * 81 / 100, Outcome = "retryable" };
        var service = Service(state, backend, clock);
        await service.RunOnceAsync(default);
        backend.Outcome = "unknown";
        await service.RunOnceAsync(default);
        await service.RunOnceAsync(default);
        await Assert.That(backend.Attempts).IsEqualTo(2);
        var ledger = await state.GetAsync(AllowanceNotifications.Collection, OrganizationId, default);
        await Assert.That(AllowanceNotifications.Read(ledger!, OrganizationId).Alerts.Single().EmailStatus).IsEqualTo("unknown");
    }

    [Test]
    public async Task UnavailableMeasurementsAndNonOwnersNeverInventAlertsOrInboxItems()
    {
        var state = await StateAsync();
        var clock = new Clock();
        var backend = new Backend(clock);
        await Service(state, backend, clock).RunOnceAsync(default);
        await Assert.That(backend.Sent.Count).IsEqualTo(0);
        backend.Operations = PlanCatalog.Limits("free").MonthlyOperations;
        await Service(state, backend, clock).RunOnceAsync(default);
        var context = await ContextAsync(state, "admin");
        await AllowanceNotificationInbox.HydrateAsync(state, context, default);
        await Assert.That(context.Account.Notifications.Count).IsEqualTo(0);
    }

    private static async Task<TestState> StateAsync()
    {
        var state = new TestState();
        await state.PutAsync(AccountService.OrganizationsCollection, OrganizationId,
            JsonSerializer.SerializeToElement(new OrganizationRecord { Id = OrganizationId }), TerraBasePrecondition.Absent, default);
        var account = new AccountRecord { Id = AccountId, UserId = UserId, Email = "owner@example.test" };
        var key = AccountService.AccountStorageKey(UserId);
        await state.PutAsync(AccountService.AccountsCollection, key, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Absent, default);
        await state.PutAsync(AccountService.IdentitiesCollection, key, JsonSerializer.SerializeToElement(new AccountIdentityBinding(UserId, AccountId)), TerraBasePrecondition.Absent, default);
        return state;
    }

    [Test]
    public async Task DocumentCountsAndNodeLocalStorageCannotTriggerPublishedAllowanceWarnings()
    {
        var state = await StateAsync();
        var clock = new Clock();
        var limits = PlanCatalog.Limits("free");
        var backend = new Backend(clock)
        {
            Operations = limits.MonthlyOperations, Bytes = limits.StorageBytes,
            OperationsContract = "document-operations-node-v1", StorageContract = "logical-bytes-node-v1"
        };
        await Assert.That(async () => await Service(state, backend, clock).RunOnceAsync(default)).Throws<IOException>();
        await Assert.That(backend.Attempts).IsEqualTo(0);
        await Assert.That(await state.GetAsync(AllowanceNotifications.Collection, OrganizationId, default)).IsNull();
    }

    private static AllowanceNotifications Service(TestState state, Backend backend, Clock clock) => new(state, backend, clock, NullLogger<AllowanceNotifications>.Instance);

    private static async Task<AccountContext> ContextAsync(TestState state, string role)
    {
        var account = AccountService.Read<AccountRecord>((await state.GetAsync(AccountService.AccountsCollection, AccountService.AccountStorageKey(UserId), default))!);
        var org = new OrganizationRecord { Id = OrganizationId, Members = new() { [UserId] = new() { UserId = UserId, Role = role } } };
        return new(new SessionRecord(UserId, "Owner", account.Email, "", null, DateTimeOffset.MaxValue), account, [org], org, null);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Backend(Clock clock) : IAllowanceNotificationBackend
    {
        public long? Operations { get; set; }
        public long? Bytes { get; set; }
        public string Outcome { get; set; } = "accepted";
        public int Attempts { get; private set; }
        public ConcurrentDictionary<string, AllowanceAlert> Sent { get; } = new();
        public Task<AllowanceUsagePage> ReadAsync(string? after, CancellationToken cancellationToken) => Task.FromResult(new AllowanceUsagePage(
            new(clock.Now.Year, clock.Now.Month, 1, 0, 0, 0, TimeSpan.Zero), clock.Now, [new(OrganizationId, Operations, Bytes)], null,
            OperationsContract, StorageContract));
        public string OperationsContract { get; set; } = AllowanceNotifications.OperationsContract;
        public string StorageContract { get; set; } = AllowanceNotifications.StorageContract;
        public Task<string> SendAsync(AllowanceAlert alert, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Outcome == "accepted") Sent.TryAdd(alert.Id, alert);
            return Task.FromResult(Outcome);
        }
    }
}
