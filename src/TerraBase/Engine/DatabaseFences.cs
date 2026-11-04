using System.Collections.Concurrent;
using TerraBase.Primitives;

namespace TerraBase.Engine;

internal sealed class DatabaseMovedException(string? home)
    : InvalidOperationException(home is null ? "the database is moving its home region; retry" : $"the database moved to {home}")
{
    public string? Home { get; } = home;
}

internal sealed class DatabaseFences
{
    private static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Fence> _fences = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _leased = new(StringComparer.Ordinal);
    private readonly string _directory;
    private long _leaseUntil;

    public DatabaseFences(string directory)
    {
        _directory = directory;
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (Path.GetFileName(file) is var name && EntityIds.IsValid(name))
                {
                    _fences[name] = new Fence(Paused: false, Home: null);
                }
            }
        }
    }

    public IReadOnlyList<string> Held => [.. Directory.Exists(_directory)
        ? Directory.EnumerateFiles(_directory).Select(Path.GetFileName).OfType<string>().Where(EntityIds.IsValid)
        : []];

    public void Pause(string database)
    {
        Directory.CreateDirectory(_directory);
        using (var file = new FileStream(PathOf(database), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Flush(flushToDisk: true);
        }

        Set(database, new Fence(Paused: true, Home: null));
    }

    public void Unresolved(string database) => Set(database, new Fence(Paused: false, Home: null));

    public void Moved(string database, string home) => Set(database, new Fence(Paused: false, Home: home));

    public void Release(string database)
    {
        if (Directory.Exists(_directory))
        {
            File.Delete(PathOf(database));
        }
    }

    public void Lift(string database)
    {
        Release(database);
        if (_fences.TryRemove(database, out var fence))
        {
            fence.Changed.TrySetResult();
        }
    }

    public async ValueTask WaitAsync(string database, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        ThrowIfUnleased(database);
        while (_fences.TryGetValue(database, out var fence))
        {
            if (!fence.Paused || waited >= MaxPause)
            {
                throw new DatabaseMovedException(fence.Home);
            }

            var started = TimeProvider.System.GetTimestamp();
            try
            {
                await fence.Changed.Task.WaitAsync(MaxPause - waited, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new DatabaseMovedException(null);
            }

            waited += TimeProvider.System.GetElapsedTime(started);
        }
    }

    public IReadOnlyCollection<string> Leased => [.. _leased.Keys];

    public void Lease(string database, bool held) => _leased[database] = held;

    public void Unlease(string database) => _leased.TryRemove(database, out _);

    public void Renewed(long until) => Volatile.Write(ref _leaseUntil, until);

    public bool Holds(string database) =>
        !_leased.TryGetValue(database, out var held) || (held && TimeProvider.System.GetTimestamp() < Volatile.Read(ref _leaseUntil));

    public string? MovedTo(string database) => _fences.TryGetValue(database, out var fence) ? fence.Home : null;

    public void ThrowIfFenced(string database)
    {
        if (_fences.TryGetValue(database, out var fence))
        {
            throw new DatabaseMovedException(fence.Home);
        }

        ThrowIfUnleased(database);
    }

    public void ThrowIfUnleased(string database)
    {
        if (!Holds(database))
        {
            throw new DatabaseMovedException(null);
        }
    }

    private void Set(string database, Fence fence)
    {
        if (_fences.TryGetValue(database, out var previous))
        {
            _fences[database] = fence;
            previous.Changed.TrySetResult();
        }
        else
        {
            _fences[database] = fence;
        }
    }

    private string PathOf(string database) => EntityIds.IsValid(database)
        ? Path.Combine(_directory, database)
        : throw new ArgumentException("a fence names a database ULID", nameof(database));

    private sealed record Fence(bool Paused, string? Home)
    {
        public TaskCompletionSource Changed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
