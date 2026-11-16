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
    private readonly string _directory;
    private readonly TimeProvider _time;

    public DatabaseFences(string directory, TimeProvider time)
    {
        _directory = directory;
        _time = time;
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (EntityIds.IsValid(name))
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

    private event Action<string>? WriterMoved;

    public IDisposable WatchMoved(string database, string? previousHome, Action moved) => new MoveWatch(this, database, previousHome, moved);

    public void Moving(string database) => WriterMoved?.Invoke(database);

    public void Moved(string database, string home)
    {
        Set(database, new Fence(Paused: false, Home: home));
        WriterMoved?.Invoke(database);
    }

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
        while (_fences.TryGetValue(database, out var fence))
        {
            if (!fence.Paused || waited >= MaxPause)
            {
                throw new DatabaseMovedException(fence.Home);
            }

            var started = _time.GetTimestamp();
            try
            {
                await fence.Changed.Task.WaitAsync(MaxPause - waited, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new DatabaseMovedException(null);
            }

            waited += _time.GetElapsedTime(started);
        }
    }

    public bool Holds(string database) => _fences.ContainsKey(database);

    public string? MovedTo(string database) => _fences.TryGetValue(database, out var fence) ? fence.Home : null;

    public void ThrowIfFenced(string database)
    {
        if (_fences.TryGetValue(database, out var fence))
        {
            throw new DatabaseMovedException(fence.Home);
        }
    }

    private void Set(string database, Fence fence)
    {
        var previous = _fences.GetValueOrDefault(database);
        _fences[database] = fence;
        previous?.Changed.TrySetResult();
    }

    private string PathOf(string database) => EntityIds.IsValid(database)
        ? Path.Combine(_directory, database)
        : throw new ArgumentException("a fence names a database ULID", nameof(database));

    private sealed class MoveWatch : IDisposable
    {
        private readonly DatabaseFences _fences;
        private readonly string _database;
        private readonly Action _moved;
        private readonly Lock _gate = new();
        private bool _disposed;

        public MoveWatch(DatabaseFences fences, string database, string? previousHome, Action moved)
        {
            (_fences, _database, _moved) = (fences, database, moved);
            fences.WriterMoved += Notify;
            if (fences.MovedTo(database) != previousHome)
            {
                Notify(database);
            }
        }

        private void Notify(string database)
        {
            lock (_gate)
            {
                if (!_disposed && database == _database)
                {
                    _moved();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _fences.WriterMoved -= Notify;
            }
        }
    }

    private sealed record Fence(bool Paused, string? Home)
    {
        public TaskCompletionSource Changed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
