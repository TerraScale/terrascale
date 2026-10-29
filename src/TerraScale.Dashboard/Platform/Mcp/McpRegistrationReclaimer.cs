using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Platform.Mcp
{
    // One bounded page per tick. The cursor is opaque and retained across iterations;
    // a bad row cannot block later rows or the rest of the collection.
    public sealed class McpRegistrationReclaimer(IDashboardState state, TimeProvider clock,
        ILogger<McpRegistrationReclaimer> logger) : BackgroundService
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private string? cursor;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
            try
            {
                do { await RunOnceAsync(stoppingToken); }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var page = await state.ReadPageAsync(McpOAuth.ClientCollection, null, 32, cursor, cancellationToken);
                foreach (var document in page.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { await McpRegistrations.ReclaimUnfinishedAsync(state, clock, document, cancellationToken); }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested) { Retry(error); }
                }
                cursor = page.NextCursor;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested) { Retry(error); }
            finally { gate.Release(); }
        }

        private void Retry(Exception error)
        {
            logger.MCPUnfinishedRegistrationCleanupIsUnavailable(error.GetType().Name);
        }
    }
}
