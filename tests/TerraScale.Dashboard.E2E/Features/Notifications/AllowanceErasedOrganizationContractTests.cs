using System.Text.Json;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.Dashboard.E2E.Features.Notifications
{
    public sealed class AllowanceErasedOrganizationContractTests
    {
        private const string Organization = "01JA00000000000000000000E1";

        [Test]
        public async Task AnErasedOrganizationInTheUsageFeedIsSkippedWithoutAWarningOrSend()
        {
            var now = DateTimeOffset.UtcNow;
            var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var backend = new Feed(new(month, now, [new(Organization, 10_000_000, 0)], null, AllowanceNotifications.OperationsContract, ""));
            var state = new ErasedState();
            var logger = new Warnings();
            await new AllowanceNotifications(state, backend, TimeProvider.System, logger).RunOnceAsync(CancellationToken.None);
            await Assert.That(logger.Count).IsEqualTo(0).Because("A deleted organization is not a failure to retry every minute.");
            await Assert.That(backend.Sent).IsEqualTo(0);
            await Assert.That(state.Writes).IsEqualTo(0);
        }

        private sealed class Feed(AllowanceUsagePage page) : IAllowanceNotificationBackend
        {
            public int Sent;
            public Task<AllowanceUsagePage> ReadAsync(string? after, CancellationToken cancellationToken)
            {
                return Task.FromResult(page);
            }

            public Task<string> SendAsync(AllowanceAlert alert, CancellationToken cancellationToken) { Interlocked.Increment(ref Sent); return Task.FromResult("accepted"); }
        }

        private sealed class Warnings : ILogger<AllowanceNotifications>
        {
            public int Count;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                {
                    Interlocked.Increment(ref Count);
                }
            }
        }

        // Only the organization record exists, and it is the deletion fence account deletion leaves behind.
        private sealed class ErasedState : IDashboardState
        {
            public int Writes;
            public Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken)
            {
                return Task.FromResult(collection == AccountService.OrganizationsCollection && id == Organization
                    ? new TerraBaseDocument(id, "v1", "value", JsonSerializer.SerializeToElement(new DeletionFence("01JA00000000000000000000F9")))
                    : null);
            }

            public Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken cancellationToken)
            { Interlocked.Increment(ref Writes); return Task.CompletedTask; }
            Task<TerraBaseDocumentPage> IDashboardState.ReadPageAsync(string collection, JsonElement? filter, int limit, string? cursor, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            Task<TerraBaseTransactionResult> IDashboardState.TransactAsync(IReadOnlyList<DashboardStateOperation> operations, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task CheckReadyAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task DeleteAsync(string collection, string id, string version, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }
        }
    }
}
