using TerraBase.Primitives;

namespace TerraBase.Engine.Revisions;

internal sealed record SealedGeneration(ulong Generation, ulong EndFrame, bool Full, IReadOnlyDictionary<uint, ulong> Pages);

internal sealed record HorizonBucket(ulong FirstGeneration, IReadOnlyList<ulong> EndFrames, IReadOnlySet<uint> Pages)
{
    public ulong LastGeneration => FirstGeneration + (ulong)EndFrames.Count - 1;
}

internal readonly record struct FileStamp(long Length, long WriteTicks);

internal sealed class LiveGeneration
{
    public Dictionary<uint, ulong> Pages { get; } = [];

    public ulong BaseFrame { get; init; }

    public ulong MaxFrame { get; set; }

    public uint CheckpointSequence { get; init; }

    public uint DatabaseSize { get; set; }

    public bool Full { get; set; }

    public static LiveGeneration After(ulong frame, uint checkpointSequence, uint databaseSize) =>
        new() { BaseFrame = frame, MaxFrame = frame, CheckpointSequence = checkpointSequence, DatabaseSize = databaseSize };
}

internal sealed record RevisionManifest(
    Ulid Incarnation,
    ulong Generation,
    (uint First, uint Second)? WalSalt,
    FileStamp? DatabaseFile,
    IReadOnlyList<SealedGeneration> Sealed,
    IReadOnlyList<HorizonBucket> Horizon)
{
    internal const int RetainedGenerations = 8;
    internal const int HorizonBuckets = 8;
    internal const int BucketGenerations = 8;
    internal const int MaxGenerationPages = 16_384;
    internal const int MaxBucketPages = 32_768;

    public static RevisionManifest NewIncarnation() => new(EntityIds.New(), 1, null, null, [], []);

    public DatabaseRevision Current(ulong frame) => new(Incarnation, Generation, frame);

    public RevisionManifest Seal(LiveGeneration live)
    {
        var full = live.Full || live.Pages.Count > MaxGenerationPages;
        var sealedGenerations = new List<SealedGeneration>(Sealed)
        {
            new(Generation, live.MaxFrame, full, full ? new Dictionary<uint, ulong>() : live.Pages.ToDictionary()),
        };
        var horizon = new List<HorizonBucket>(Horizon);
        while (sealedGenerations.Count > RetainedGenerations)
        {
            var oldest = sealedGenerations[0];
            sealedGenerations.RemoveAt(0);
            if (oldest.Full)
            {
                horizon.Clear();
                continue;
            }

            if (horizon.Count == 0
                || horizon[^1].EndFrames.Count >= BucketGenerations
                || horizon[^1].Pages.Count + oldest.Pages.Count > MaxBucketPages)
            {
                horizon.Add(new HorizonBucket(oldest.Generation, [], new HashSet<uint>()));
            }

            var bucket = horizon[^1];
            var pages = new HashSet<uint>(bucket.Pages);
            pages.UnionWith(oldest.Pages.Keys);
            horizon[^1] = bucket with { EndFrames = [.. bucket.EndFrames, oldest.EndFrame], Pages = pages };
            if (horizon.Count > HorizonBuckets)
            {
                horizon.RemoveRange(0, horizon.Count - HorizonBuckets);
            }
        }

        return this with { Generation = Generation + 1, WalSalt = null, Sealed = sealedGenerations, Horizon = horizon };
    }

    public RevisionManifest RewoundFull() => this with
    {
        Sealed = [.. Sealed.Select(entry => entry with { Full = true, Pages = new Dictionary<uint, ulong>() })],
        Horizon = [],
    };
}
