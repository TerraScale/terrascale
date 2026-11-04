using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

internal interface IReplicaShapeReader : IAsyncDisposable
{
    ValueTask<LineageResult> ResolveAsync(string table, ShapeQuery query, CancellationToken cancellationToken);

    ValueTask<ShapeRead?> ReadAsync(ShapeScope scope, ShapeLineage lineage, long offset, IMemoryBudget memory, CancellationToken cancellationToken);
}

internal sealed class ReplicaShapeTails(IDatabaseReplicas replicas, IMemoryBudget memory, TimeProvider time, IShapeFlights<ShapeRead> flights, ShapeControls controls, IShapeMirror mirror) : IDisposable
{
    private readonly SemaphoreSlim _waiting = new(128, 128);

    public void Dispose() => _waiting.Dispose();

    internal bool Holds(TenantContext database) => replicas.Holds(database);

    internal async ValueTask<LineageResult> ResolveAsync(TenantContext database, string table, ShapeQuery query, CancellationToken cancellationToken)
    {
        if (replicas.HeldRevision(database) is not { } revision)
        {
            return new Unavailable("the region replica is catching up; retry later");
        }

        return await controls.ReadAsync(replicas, revision, database, table, query, Read, cancellationToken).ConfigureAwait(false);

        async ValueTask<LineageResult> Read(CancellationToken token)
        {
            await using var reader = await replicas.BeginShapeReadAsync(database, token).ConfigureAwait(false);
            return reader is null ? new Unavailable("the region replica is catching up; retry later")
                : await reader.ResolveAsync(table, query, token).ConfigureAwait(false);
        }
    }

    internal async ValueTask<ShapeRead> WaitAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken)
    {
        var request = new ShapeFlightRequest(scope.Database, lineage.Handle, offset.ToString(System.Globalization.CultureInfo.InvariantCulture), true);
        return ShapeService.Join(await flights.ReadAsync(request, Read, cancellationToken).ConfigureAwait(false));

        async ValueTask<ShapeRead> Read(CancellationToken token) =>
            await ShapeService.MirrorTailAsync(scope, lineage, offset,
                ShapeService.Detach(await WaitCoreAsync(scope, lineage, offset, token).ConfigureAwait(false)), mirror, time, token).ConfigureAwait(false);
    }

    private async ValueTask<ShapeRead> WaitCoreAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken)
    {
        if (!await _waiting.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new QuotaExceeded("shape reads are at capacity; retry later", "shape_busy");
        }

        try
        {
            using var admission = memory.TryReserve(MemoryLane.Foreground, 16 * 1024, scope.Database) ?? throw new QosMemoryException();
            var started = time.GetTimestamp();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50), time);
            while (true)
            {
                ShapeRead? answer;
                await using (var reader = await replicas.BeginShapeReadAsync(scope.Database, cancellationToken).ConfigureAwait(false))
                {
                    if (reader is null)
                    {
                        return new Unavailable("the region replica is catching up; retry later");
                    }

                    answer = await reader.ReadAsync(scope, lineage, offset, memory, cancellationToken).ConfigureAwait(false);
                }

                if (answer is ShapeBody body)
                {
                    if (body.NextOffset != offset.ToString(System.Globalization.CultureInfo.InvariantCulture) || time.GetElapsedTime(started) >= TimeSpan.FromSeconds(20))
                    {
                        return body;
                    }

                    body.Lease?.Dispose();
                }
                else if (answer is { } failure)
                {
                    return failure;
                }
                else if (time.GetElapsedTime(started) >= TimeSpan.FromSeconds(20))
                {
                    return new Unavailable("the region replica has not reached the requested offset; retry later");
                }

                _ = await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is StorageBusyException or QosMemoryException)
        {
            return new QuotaExceeded("shape reads are at capacity; retry later", "shape_busy");
        }
        catch (ShapeTooLargeException)
        {
            return new Unavailable("a commit exceeds 8 MiB of shape operations", "shape_too_large");
        }
        finally
        {
            _waiting.Release();
        }
    }
}
