using System.Runtime.InteropServices;

namespace TerraScale.Dashboard.Features.Billing;

internal sealed class KeyedGate
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Turn> _turns = new(StringComparer.Ordinal);

    public async Task<T> RunAsync<T>(string key, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        Turn turn;
        lock (_gate)
        {
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_turns, key, out _);
            slot ??= new Turn();
            slot.Users++;
            turn = slot;
        }

        try
        {
            await turn.Semaphore.WaitAsync(cancellationToken);
            try
            {
                return await work();
            }
            finally
            {
                turn.Semaphore.Release();
            }
        }
        finally
        {
            lock (_gate)
            {
                if (--turn.Users == 0)
                {
                    _turns.Remove(key);
                    turn.Semaphore.Dispose();
                }
            }
        }
    }

    private sealed class Turn
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }
}
