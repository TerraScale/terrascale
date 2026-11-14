using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using TerraBase.Primitives;

namespace TerraBase.Engine;

internal sealed class DatabaseMovedException(string? home)
    : InvalidOperationException(home is null ? "the database is moving its home region; retry" : $"the database moved to {home}")
{
    public string? Home { get; } = home;
}

internal sealed record PromotionSource(string Region, string Node, long Version);

internal sealed class DatabaseFences
{
    private const string PromotingDirectory = "promoting";

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

        var promoting = Path.Combine(directory, PromotingDirectory);
        if (Directory.Exists(promoting))
        {
            foreach (var file in Directory.EnumerateFiles(promoting))
            {
                var name = Path.GetFileName(file);
                if (EntityIds.IsValid(name) && File.ReadAllLines(file) is [var region, var node, var version]
                    && long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
                {
                    _fences[name] = new Fence(Paused: false, Home: null, new PromotionSource(region, node, epoch));
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

    public void Promote(string database, PromotionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = PromotionPathOf(database);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{source.Region}\n{source.Node}\n{source.Version}\n")));
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
        Set(database, new Fence(Paused: false, Home: null, source));
    }

    public PromotionSource? Promoting(string database) => _fences.TryGetValue(database, out var fence) ? fence.Promotion : null;

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
        if (Directory.Exists(Path.Combine(_directory, PromotingDirectory)))
        {
            File.Delete(PromotionPathOf(database));
        }

        if (_fences.TryRemove(database, out var fence))
        {
            fence.Changed.TrySetResult();
        }
    }

    public async ValueTask WaitAsync(string database, bool promotion, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        while (_fences.TryGetValue(database, out var fence))
        {
            if (promotion && fence.Promotion is not null)
            {
                return;
            }

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

    public void ThrowIfFenced(string database, bool promotion = false)
    {
        if (_fences.TryGetValue(database, out var fence) && !(promotion && fence.Promotion is not null))
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

    private string PromotionPathOf(string database) => EntityIds.IsValid(database)
        ? Path.Combine(_directory, PromotingDirectory, database)
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

    private sealed record Fence(bool Paused, string? Home, PromotionSource? Promotion = null)
    {
        public TaskCompletionSource Changed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
