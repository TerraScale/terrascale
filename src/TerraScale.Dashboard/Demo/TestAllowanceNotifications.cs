using TerraScale.Dashboard.Features.Notifications;

namespace TerraScale.Dashboard.Demo;

// Test hosts never reach the production allowance feed or send product mail.
sealed class TestAllowanceNotifications(TimeProvider clock) : IAllowanceNotificationBackend
{
    public Task<AllowanceUsagePage> ReadAsync(string? after, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return Task.FromResult(new AllowanceUsagePage(new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero), now, [], null,
            AllowanceNotifications.OperationsContract, AllowanceNotifications.StorageContract));
    }

    public Task<string> SendAsync(AllowanceAlert alert, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The empty test allowance feed cannot send email.");
}
