using System.Globalization;
using System.Text;
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

    ValueTask<ShapeGrant?> ReadGrantAsync(string table, CancellationToken cancellationToken);

    ValueTask<ShapeProvisional> ProvisionalAsync(ShapeScope scope, ShapeLineage lineage, IReadOnlyList<ProvisionalTransaction> pending, IMemoryBudget memory,
        CancellationToken cancellationToken);
}

internal sealed class ReplicaShapeTails(IDatabaseReplicas replicas, IMemoryBudget memory, TimeProvider time, IShapeFlights<ShapeRead> flights, ShapeControls controls,
    ShapeOverlay overlay) : IDisposable
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

    internal async ValueTask<ShapeGrantRead> GrantAsync(TenantContext database, string table, CancellationToken cancellationToken)
    {
        await using var reader = await replicas.BeginShapeReadAsync(database, cancellationToken).ConfigureAwait(false);
        if (reader is null)
        {
            return new Unavailable("the region replica is catching up; retry later");
        }

        return await reader.ReadGrantAsync(table, cancellationToken).ConfigureAwait(false) is { } grant
            ? grant
            : new NotFound("the table has no read grant", "no_read_grant");
    }

    internal async ValueTask<ShapeRead> WaitAsync(ShapeScope scope, ShapeLineage lineage, long offset, string? local, CancellationToken cancellationToken)
    {
        var at = offset.ToString(CultureInfo.InvariantCulture);
        var request = new ShapeFlightRequest(scope.Database, lineage.Handle, local is null ? at : at + "~" + local, true);
        return ShapeService.Join(await flights.ReadAsync(request, Read, cancellationToken).ConfigureAwait(false));

        async ValueTask<ShapeRead> Read(CancellationToken token) =>
            ShapeService.Detach(await WaitCoreAsync(scope, lineage, offset, local, token).ConfigureAwait(false));
    }

    private async ValueTask<ShapeRead> WaitCoreAsync(ShapeScope scope, ShapeLineage lineage, long offset, string? local, CancellationToken cancellationToken)
    {
        if (!await _waiting.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new QuotaExceeded("shape reads are at capacity; retry later", "shape_busy");
        }

        try
        {
            using var admission = memory.TryReserve(MemoryLane.Foreground, 16 * 1024, scope.Database) ?? throw new QosMemoryException();
            var started = time.GetTimestamp();
            var at = offset.ToString(CultureInfo.InvariantCulture);
            while (true)
            {
                var changed = local is null ? null : overlay.Changed(scope.Database);
                ShapeRead? answer;
                ShapeProvisional? provisional = null;
                await using (var reader = await replicas.BeginShapeReadAsync(scope.Database, cancellationToken).ConfigureAwait(false))
                {
                    if (reader is null)
                    {
                        return new Unavailable("the region replica is catching up; retry later");
                    }

                    answer = await reader.ReadAsync(scope, lineage, offset, memory, cancellationToken).ConfigureAwait(false);
                    if (local is not null && answer is ShapeBody { UpToDate: true })
                    {
                        provisional = await reader.ProvisionalAsync(scope, lineage, overlay.Snapshot(scope.Database), memory, cancellationToken).ConfigureAwait(false);
                        overlay.Held(scope.Database, provisional.Held);
                    }
                }

                var timedOut = time.GetElapsedTime(started) >= TimeSpan.FromSeconds(20);
                if (answer is ShapeBody body)
                {
                    if (body.NextOffset != at || timedOut || (provisional is not null && provisional.Digest != local))
                    {
                        return provisional is null ? body : WithProvisional(body, provisional);
                    }

                    body.Lease?.Dispose();
                }
                else if (answer is { } failure)
                {
                    return failure;
                }
                else if (timedOut)
                {
                    return new Unavailable("the region replica has not reached the requested offset; retry later");
                }

                var tick = Task.Delay(TimeSpan.FromMilliseconds(50), time, cancellationToken);
                _ = await Task.WhenAny(tick, changed ?? tick).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
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

    private static ShapeBody WithProvisional(ShapeBody body, ShapeProvisional provisional)
    {
        using (body.Lease)
        {
            var json = body.Json.Span;
            var digest = Encoding.ASCII.GetBytes(provisional.Digest);
            var joined = new byte[json.Length - 1 + 16 + provisional.Json.Length + 11 + digest.Length + 2];
            var written = 0;
            Append(json[..^1]);
            Append(",\"provisional\":"u8);
            Append(provisional.Json);
            Append(",\"local\":\""u8);
            Append(digest);
            Append("\"}"u8);
            return new ShapeBody(joined.AsMemory(0, written), body.NextOffset, body.UpToDate, Manifest: body.Manifest, Local: provisional.Digest);

            void Append(ReadOnlySpan<byte> bytes)
            {
                bytes.CopyTo(joined.AsSpan(written));
                written += bytes.Length;
            }
        }
    }
}
