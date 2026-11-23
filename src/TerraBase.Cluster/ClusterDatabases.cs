using System.Collections.Concurrent;
using System.Threading.Channels;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Cluster;

public delegate ValueTask<Result<ISqlDatabaseSession, NotFound, Unavailable>> DatabaseSessionResolver(Ulid databaseId, CancellationToken cancellationToken);

public sealed class ClusterDatabases(DatabaseSessionResolver resolver)
{
    private const int CatchUpQueueCapacity = 1024;

    private readonly ConcurrentDictionary<Ulid, CausalBookmark> _bookmarks = new();
    private readonly Channel<Ulid> _catchUp = Channel.CreateBounded<Ulid>(new BoundedChannelOptions(CatchUpQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    public IReadOnlyCollection<Ulid> Tracked => [.. _bookmarks.Keys];

    internal ChannelReader<Ulid> CatchUpRequests => _catchUp.Reader;

    public void Track(Ulid databaseId) => _ = _bookmarks.TryAdd(databaseId, CausalBookmark.Empty);

    public CausalBookmark Current(Ulid databaseId) => _bookmarks.GetValueOrDefault(databaseId, CausalBookmark.Empty);

    public void Observe(Ulid databaseId, CausalBookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        _ = _bookmarks.AddOrUpdate(databaseId, bookmark, (_, existing) => existing.Merge(bookmark));
    }

    public bool RequestCatchUp(Ulid databaseId)
    {
        Track(databaseId);
        return _catchUp.Writer.TryWrite(databaseId);
    }

    public async ValueTask<Result<ISqlDatabaseSession, NotFound, Unavailable>> ResolveAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var resolved = await resolver(databaseId, cancellationToken).ConfigureAwait(false);
        return resolved switch
        {
            ISqlDatabaseSession session => Observed(session),
            NotFound notFound => notFound,
            Unavailable unavailable => unavailable,
        };
    }

    private Result<ISqlDatabaseSession, NotFound, Unavailable> Observed(ISqlDatabaseSession session)
    {
        Observe(session.DatabaseId, session.CurrentBookmark);
        return session;
    }
}

public sealed class ChangefeedPublisher(IEnumerable<IChangefeedHub> hubs, TimeProvider timeProvider)
{
    private readonly IChangefeedHub[] _hubs = [.. hubs];

    public bool IsEnabled => _hubs.Length > 0;

    public async ValueTask PublishAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (_hubs.Length == 0 || commit.Sequence == 0)
        {
            return;
        }

        var entry = new ChangefeedEntry(
            databaseId,
            commit.TxId,
            commit.OriginRegion,
            commit.Sequence,
            commit.SchemaEpoch,
            commit.Mode,
            commit.DeltaPayload,
            commit.Hlc.WallMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(commit.Hlc.WallMs) : timeProvider.GetUtcNow());
        foreach (var hub in _hubs)
        {
            await hub.PublishAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }
}
