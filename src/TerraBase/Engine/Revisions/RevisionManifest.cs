using TerraBase.Primitives;

namespace TerraBase.Engine.Revisions;

internal sealed record SealedGeneration(ulong Generation, ulong EndFrame, bool Full, IReadOnlyDictionary<uint, ulong> Pages);

internal sealed class HorizonBucket(ulong firstGeneration)
{
    public ulong FirstGeneration { get; } = firstGeneration;

    public List<ulong> EndFrames { get; } = [];

    public HashSet<uint> Pages { get; } = [];

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

internal sealed class RevisionManifest
{
    internal const int RetainedGenerations = 8;
    internal const int HorizonBuckets = 8;
    internal const int BucketGenerations = 8;
    internal const int MaxGenerationPages = 16_384;
    internal const int MaxBucketPages = 32_768;

    public required Ulid Incarnation { get; init; }

    public ulong Generation { get; set; } = 1;

    public (uint First, uint Second)? WalSalt { get; set; }

    public FileStamp? DatabaseFile { get; set; }

    public List<SealedGeneration> Sealed { get; } = [];

    public List<HorizonBucket> Horizon { get; } = [];

    public static RevisionManifest NewIncarnation() => new() { Incarnation = EntityIds.New() };

    public DatabaseRevision Current(ulong frame) => new(Incarnation, Generation, frame);

    public void Seal(LiveGeneration live)
    {
        var full = live.Full || live.Pages.Count > MaxGenerationPages;
        Sealed.Add(new SealedGeneration(Generation, live.MaxFrame, full, live.Pages.Where(_ => !full).ToDictionary()));
        Generation++;
        WalSalt = null;
        while (Sealed.Count > RetainedGenerations)
        {
            var oldest = Sealed[0];
            Sealed.RemoveAt(0);
            if (oldest.Full)
            {
                Horizon.Clear();
                continue;
            }

            if (Horizon.Count == 0
                || Horizon[^1].EndFrames.Count >= BucketGenerations
                || Horizon[^1].Pages.Count + oldest.Pages.Count > MaxBucketPages)
            {
                Horizon.Add(new HorizonBucket(oldest.Generation));
            }

            var bucket = Horizon[^1];
            bucket.EndFrames.Add(oldest.EndFrame);
            bucket.Pages.UnionWith(oldest.Pages.Keys);
            if (Horizon.Count > HorizonBuckets)
            {
                Horizon.RemoveRange(0, Horizon.Count - HorizonBuckets);
            }
        }
    }
}
