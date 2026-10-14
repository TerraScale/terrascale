using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Features.Qos;

namespace TerraBase.Features.Shapes;

/// <summary>Engine-implemented resume target read, in the same transaction as the cursor checks.</summary>
internal interface IShapeResumeStorage
{
    /// <summary>
    /// The commit boundary before the first row of <paramref name="collection"/> at or below <paramref name="head"/> whose
    /// origin stamp is outside the proven prefix of <paramref name="covered"/> and <paramref name="prefixes"/>, or <paramref name="head"/> with none; null when that boundary is unproven.
    /// </summary>
    ValueTask<long?> ReadShapeResumeTargetAsync(string collection, IReadOnlyDictionary<ReplicaId, Hlc> covered, IReadOnlyDictionary<ReplicaId, FeedPrefix> prefixes, long head,
        CancellationToken cancellationToken);
}

/// <summary>
/// Cross-machine resumes (shape-reads section 9, FR-23): proves the cursor's prefix on this machine and redirects to the local
/// commit boundary before the first row it does not cover. Delivery is at least once; nothing is skipped on a stamp alone.
/// </summary>
internal sealed class ShapeResume(IStorageEngine engine, IShapeStorage storage, IShapeRetention retention, TimeProvider time) : IShapeResume
{
    /// <inheritdoc />
    public async ValueTask<ResumeResult> ResolveAsync(ShapeScope scope, ShapeLineage lineage, ReadOnlyMemory<byte> scope16, FeedCursor cursor,
        CancellationToken cancellationToken)
    {
        // Rule 0: another organization, project, database, collection generation, filter or wire format.
        if (!scope16.Span.SequenceEqual(ShapeService.ResumeScope(scope.Database, scope.Collection, scope.CanonicalFilter)))
        {
            return new MustRefetch("the resume cursor names another shape");
        }

        // Rule 1, as R8's 410 cursor_expired: an old cursor may lack an origin whose events all aged out since.
        var instant = time.GetUtcNow();
        if (cursor.Issued.Value < FeedCursor.At(instant - FeedCursor.Retention).Value)
        {
            return new MustRefetch("the resume cursor expired");
        }

        // An issue instant in the future counts as now, as R8 does.
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
                // Rule 1: retention deleted a promised event, or this machine's prefix differs from the issuer's.
                var proof = await transaction.ReadFeedAsync(cursor, new HashSet<string>(StringComparer.Ordinal) { scope.Collection.Name }, 0, 0,
                    cancellationToken).ConfigureAwait(false);
                if (proof.Expired)
                {
                    return new MustRefetch("the resume prefix cannot be proven on this machine");
                }

                // Rule 2: behind, so rows the client holds may not be here yet.
                var local = await transaction.GetFeedHeadAsync(cancellationToken).ConfigureAwait(false);
                if (cursor.Stamps.Any(pair => !local.TryGetValue(pair.Key, out var stamp) || stamp.Value < pair.Value.Value))
                {
                    return new MustRefetch("this machine is behind the resume cursor");
                }

                // Rules 3 and 4.
                head = await ((IShapeFreshStorage)transaction).ReadShapeHeadAsync(cancellationToken).ConfigureAwait(false);
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

/// <summary>Registers <see cref="ShapeResume"/> in place of the planned gate.</summary>
internal static class ShapeResumeExtensions
{
    public static IServiceCollection AddTerraBaseShapeResume(this IServiceCollection services) =>
        services.AddSingleton<IShapeResume, ShapeResume>();
}
