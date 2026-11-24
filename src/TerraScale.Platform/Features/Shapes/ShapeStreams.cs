using System.Runtime.InteropServices;

namespace TerraScale.Platform.Features.Shapes;

public sealed class ShapeStreams
{
    public const int PerProject = 1024;
    private readonly Lock _gate = new();
    private readonly Dictionary<Ulid, int> _open = [];

    public ShapeStreamLease? TryOpen(Ulid project)
    {
        lock (_gate)
        {
            ref var open = ref CollectionsMarshal.GetValueRefOrAddDefault(_open, project, out _);
            if (open >= PerProject)
            {
                return null;
            }

            open++;
            return new ShapeStreamLease(this, project);
        }
    }

    internal void Close(Ulid project)
    {
        lock (_gate)
        {
            var open = _open[project] - 1;
            if (open == 0)
            {
                _ = _open.Remove(project);
            }
            else
            {
                _open[project] = open;
            }
        }
    }
}

public sealed class ShapeStreamLease(ShapeStreams streams, Ulid project) : IDisposable
{
    private int _closed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            streams.Close(project);
        }
    }
}
