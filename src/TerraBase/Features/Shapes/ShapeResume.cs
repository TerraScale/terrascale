using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Features.Qos;

namespace TerraBase.Features.Shapes;

internal interface IShapeResumeStorage
{
    ValueTask<long?> ReadShapeResumeTargetAsync(string collection, IReadOnlyDictionary<ReplicaId, Hlc> covered, IReadOnlyDictionary<ReplicaId, FeedPrefix> prefixes, long head,
        CancellationToken cancellationToken);
}
internal sealed class ShapeResume(IStorageEngine engine, IShapeStorage storage, IShapeRetention retention, TimeProvider time) : IShapeResume
{
    public async ValueTask<ResumeResult> ResolveAsync(ShapeScope scope, ShapeLineage lineage, ReadOnlyMemory<byte> scope16, FeedCursor cursor,
        CancellationToken cancellationToken)
    {
        if (!scope16.Span.SequenceEqual(ShapeService.ResumeScope(scope.Database, scope.Collection, scope.CanonicalFilter)))
        {
            return new MustRefetch("the resume cursor names another shape");
        }
        var instant = time.GetUtcNow();
        if (cursor.Issued.Value < FeedCursor.At(instant - FeedCursor.Retention).Value)
        {
            return new MustRefetch("the resume cursor expired");
        }
        if (cursor.Issued.Value > FeedCursor.At(instant).Value)
        {
            cursor = cursor with { Issued = FeedCursor.At(instant) };
        }

        try
        {
            if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is not { } store)
            {
                return new Unavailable("the database is closing; retry later");
            }

            var transaction = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
            long head;
            long? target;
            await using (transaction.ConfigureAwait(false))
            {
                var proof = await transaction.ReadFeedAsync(cursor, new HashSet<string>(StringComparer.Ordinal) { scope.Collection.Name }, 0, 0,
                    cancellationToken).ConfigureAwait(false);
                if (proof.Expired)
                {
                    return new MustRefetch("the resume prefix cannot be proven on this machine");
                }
                var local = await transaction.GetFeedHeadAsync(cancellationToken).ConfigureAwait(false);
                if (cursor.Stamps.Any(pair => !local.TryGetValue(pair.Key, out var stamp) || stamp.Value < pair.Value.Value))
                {
                    return new MustRefetch("this machine is behind the resume cursor");
                }
                head = await ((IShapeStorageTransaction)transaction).ReadShapeFrontierAsync(cancellationToken).ConfigureAwait(false);
                target = await ((IShapeResumeStorage)transaction).ReadShapeResumeTargetAsync(scope.Collection.Name, cursor.Stamps, cursor.Prefixes, head,
                    cancellationToken).ConfigureAwait(false);
            }

            if (target is not { } offset)
            {
                return new MustRefetch("the resume target is not a proven commit boundary");
            }

            if (await retention.CheckOffsetAsync(store, lineage.Handle, offset, cancellationToken).ConfigureAwait(false) is not null)
            {
                return new MustRefetch("the resume target is below this machine's retention floor");
            }

            return offset;
        }
        catch (Exception error) when (error is ObjectDisposedException or StorageBusyException or QosMemoryException)
        {
            return new Unavailable("shape storage changed or is busy; retry later");
        }
    }
}
internal static class ShapeResumeExtensions
{
    public static IServiceCollection AddTerraBaseShapeResume(this IServiceCollection services) =>
        services.AddSingleton<IShapeResume, ShapeResume>();
}
