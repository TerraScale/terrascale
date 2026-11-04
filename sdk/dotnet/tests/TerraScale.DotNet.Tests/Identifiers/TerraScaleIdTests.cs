using NUlid;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Identifiers;

public sealed class TerraScaleIdTests
{
    private const string Id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1791475200000);

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FA")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAV0")]
    [Arguments("01arz3ndektsv4rrffq69g5fav")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAv")]
    [Arguments("acct_01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [Arguments("org_01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [Arguments("doc_01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [Arguments("prj_01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [Arguments("018f0000-0000-7000-8000-000000000001")]
    [Arguments("018f0000000070008000000000000001")]
    [Arguments(" 01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAV ")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FA\n")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAI")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAL")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAO")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAU")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAé")]
    [Arguments("8ZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    [Arguments("ZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public async Task NonCanonicalIdsAreRejectedWithoutNormalization(string? id)
    {
        await Assert.That(TerraScaleIds.IsValid(id)).IsFalse();
        var error = await Assert.That(() => TerraScaleIds.Validate(id)).Throws<ArgumentException>();
        await Assert.That(error!.ParamName).IsEqualTo(nameof(id));
    }

    [Test]
    public async Task SameMillisecondIdsAreCanonicalUniqueAndStrictlyIncreasing()
    {
        var generator = new TerraScaleIdGenerator(new Clock(Timestamp));
        var previous = generator.NewId();
        await Assert.That(Ulid.Parse(previous).Time).IsEqualTo(Timestamp);
        for (var index = 0; index < 1000; index++)
        {
            var next = generator.NewId();
            await Assert.That(TerraScaleIds.IsValid(next)).IsTrue();
            await Assert.That(string.CompareOrdinal(previous, next)).IsLessThan(0);
            await Assert.That(Ulid.Parse(next).Time).IsEqualTo(Timestamp);
            previous = next;
        }
    }

    [Test]
    public async Task ConcurrentCallsToOneGeneratorProduceDistinctCanonicalIds()
    {
        var generator = new TerraScaleIdGenerator(new Clock(Timestamp));
        var ids = new string[1000];
        await Parallel.ForEachAsync(Enumerable.Range(0, ids.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 8 }, (index, _) =>
            {
                ids[index] = generator.NewId();
                return ValueTask.CompletedTask;
            });

        await Assert.That(ids.All(TerraScaleIds.IsValid)).IsTrue();
        await Assert.That(ids.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(ids.Length);
    }

    [Test]
    public async Task SystemClockGeneratorProducesAPlainUlid()
    {
        var generator = new TerraScaleIdGenerator();
        await Assert.That(TerraScaleIds.IsValid(generator.NewId())).IsTrue();
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
