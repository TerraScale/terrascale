using System.Collections.Concurrent;
using System.Security.Cryptography;
using TerraBase.Postgres.Hosting;

namespace TerraBase.Postgres;

internal interface IPostgresCancellable
{
    int SecretKey { get; }

    void Cancel();
}

public sealed class PostgresBackendRegistry
{
    private readonly ConcurrentDictionary<int, IPostgresCancellable> _backends = new();
    private int _connections;

    public int ActiveConnections => Volatile.Read(ref _connections);

    internal bool TryAdmit()
    {
        if (Interlocked.Increment(ref _connections) <= PostgresLimits.MaxConnections)
        {
            return true;
        }

        Interlocked.Decrement(ref _connections);
        return false;
    }

    internal void Release() => Interlocked.Decrement(ref _connections);

    internal int Allocate(IPostgresCancellable backend)
    {
        while (true)
        {
            var processId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            if (_backends.TryAdd(processId, backend))
            {
                return processId;
            }
        }
    }

    internal void Remove(int processId) => _backends.TryRemove(processId, out _);

    internal void Cancel(int processId, int secretKey)
    {
        if (_backends.TryGetValue(processId, out var backend) && CryptographicOperations.FixedTimeEquals(BitConverter.GetBytes(backend.SecretKey), BitConverter.GetBytes(secretKey)))
        {
            backend.Cancel();
        }
    }
}
