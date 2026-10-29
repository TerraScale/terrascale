using System.Collections.Concurrent;
using TerraScale.Dashboard.Features.Notifications;

namespace TerraScale.Dashboard.E2E.Demo
{
    // Test hosts never reach the production allowance feed or send product mail.
    sealed class TestAllowanceNotifications(TimeProvider clock) : IAllowanceNotificationBackend
    {
        private readonly object gate = new();
        private readonly SemaphoreSlim reads = new(0);
        private AllowanceUsageObservation? observation;
        private string operationsContract = AllowanceNotifications.OperationsContract;
        // A claimed storage contract must never enable storage notices before authoritative metering exists.
        private string storageContract = "storage-bytes-regions-global-v1";
        private string outcome = "accepted";
        private bool deliveryConfigured;
        public ConcurrentQueue<AllowanceAlert> Attempts { get; } = new();
        public ConcurrentDictionary<string, AllowanceAlert> Sent { get; } = new();

        internal void Observe(AllowanceUsageObservation? value, string delivery = "accepted", bool supported = true, string? operationContract = null)
        {
            lock (gate)
            {
                observation = value;
                deliveryConfigured = value is not null;
                outcome = delivery;
                operationsContract = operationContract ?? (supported ? AllowanceNotifications.OperationsContract : "document-operations-node-v1");
                storageContract = supported ? "storage-bytes-regions-global-v1" : "logical-bytes-node-v1";
            }
        }

        internal void ClearObservation()
        {
            lock (gate)
            {
                observation = null;
            }
        }

        internal Task WaitForReadAsync(CancellationToken cancellationToken)
        {
            return reads.WaitAsync(cancellationToken);
        }

        public Task<AllowanceUsagePage> ReadAsync(string? after, CancellationToken cancellationToken)
        {
            AllowanceUsagePage page;
            lock (gate)
            {
                var now = clock.GetUtcNow();
                page = new(new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero), now,
                    observation is { } value ? [value] : [], null, operationsContract, storageContract);
            }
            reads.Release();
            return Task.FromResult(page);
        }

        public Task<string> SendAsync(AllowanceAlert alert, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (!deliveryConfigured)
                {
                    throw new InvalidOperationException("The empty test allowance feed cannot send email.");
                }

                Attempts.Enqueue(alert);
                if (outcome == "accepted")
                {
                    Sent.TryAdd(alert.Id, alert);
                }

                return Task.FromResult(outcome);
            }
        }
    }
}
