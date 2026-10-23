using TerraBase.Engine;

namespace TerraBase.Features.Replication;

/// <summary>Completed work across one batch's transactions; progress never certifies a commit or remote durability.</summary>
public sealed class ApplyProgress
{
    private readonly Lock _gate = new();
    private IStorageTransaction? _active;
    private long _completed;

    internal long Reported { get; set; }

    internal long Value
    {
        get
        {
            lock (_gate)
            {
                return _completed + (_active?.Work ?? 0);
            }
        }
    }

    internal void Attach(IStorageTransaction transaction)
    {
        lock (_gate)
        {
            _active = transaction;
        }
    }

    internal void Commit()
    {
        lock (_gate)
        {
            _completed += _active?.Work ?? 0;
            _active = null;
        }
    }
}
