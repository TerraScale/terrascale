using System.Globalization;
using TerraBase.Errors;

namespace TerraBase.Features.Management.Usage;

/// <summary>A bucket size of the usage series: UTC hours or UTC days.</summary>
/// <param name="Name">The <c>granularity</c> query value.</param>
/// <param name="Hours">Hours per bucket.</param>
/// <param name="Kept">Buckets the catalog keeps, and one request reads at most.</param>
/// <param name="Format">Bucket document id format, which sorts as the bucket's start.</param>
public sealed record UsageGranularity(string Name, int Hours, int Kept, string Format)
{
    /// <summary>Minutes per bucket, including the minute analytics granularity.</summary>
    public int Minutes { get; init; } = Hours * 60;

    /// <summary>The state collection of a database that holds these buckets.</summary>
    public string Collection => "usage-" + Name + "s";

    /// <summary>The start of <paramref name="bucket"/>, counted in buckets since the Unix epoch.</summary>
    public DateTimeOffset Start(long bucket) => DateTimeOffset.UnixEpoch.AddMinutes(bucket * Minutes);

    /// <summary>The document id of <paramref name="bucket"/>.</summary>
    public string Id(long bucket) => Start(bucket).ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>The bucket of a document id.</summary>
    public long Parse(string id) =>
        DateTimeOffset.ParseExact(id, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds() / 60 / Minutes;
}

/// <summary>The period a usage request reads: <paramref name="Count"/> buckets from bucket <paramref name="First"/>.</summary>
/// <param name="Granularity">Bucket size.</param>
/// <param name="First">First bucket, counted in buckets since the Unix epoch.</param>
/// <param name="Count">Buckets, 1 to <see cref="UsageGranularity.Kept"/>.</param>
public sealed record UsageWindow(UsageGranularity Granularity, long First, int Count)
{
    /// <summary>Minute buckets, kept two hours.</summary>
    public static UsageGranularity Minute { get; } = new("minute", 0, 120, "yyyy-MM-dd'T'HH:mm") { Minutes = 1 };

    /// <summary>Hourly buckets, kept 35 days.</summary>
    public static UsageGranularity Hour { get; } = new("hour", 1, 35 * 24, "yyyy-MM-dd'T'HH");

    /// <summary>Daily buckets, kept 400 days.</summary>
    public static UsageGranularity Day { get; } = new("day", 24, 400, "yyyy-MM-dd");

    // Instants a request may name end before this.
    private static readonly DateTimeOffset Latest = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // RFC 3339 date-times: a full date and time, optional fraction, and Z or a numeric offset.
    private static readonly string[] Rfc3339 =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    /// <summary>Every granularity; each flush saves counts at all of them.</summary>
    public static IReadOnlyList<UsageGranularity> Granularities { get; } = [Minute, Hour, Day];

    /// <summary>Query parameters of every usage route.</summary>
    public const string Description =
        "Query: granularity (hour, the default, or day: UTC buckets), from (inclusive, rounded down to its bucket) and to (exclusive, "
        + "rounded up; default now), as RFC 3339 instants. Without from, the period is the 24 hours or 30 days that end with to. "
        + "A period holds at most 840 hourly or 400 daily buckets, which is how long they are kept.";

    /// <summary>Start of the first bucket.</summary>
    public DateTimeOffset From => Granularity.Start(First);

    /// <summary>End of the last bucket, exclusive.</summary>
    public DateTimeOffset To => Granularity.Start(First + Count);

    /// <summary>The position in this window of the bucket holding <paramref name="hour"/> (hours since the Unix epoch).</summary>
    public int? IndexOf(long hour, int minute = 0) => ((hour * 60 + minute) / Granularity.Minutes) - First is var index && index >= 0 && index < Count ? (int)index : null;

    /// <summary>The window of a usage request's <c>granularity</c>, <c>from</c> and <c>to</c> query parameters.</summary>
    public static Valid<UsageWindow> Parse(IQueryCollection query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        var granularity = query["granularity"].ToString() switch
        {
            "" or "hour" => Hour,
            "day" => Day,
            _ => null,
        };
        if (granularity is null)
        {
            return new Validation("granularity must be hour or day");
        }

        if (!Instant(query, "to", now, out var to) || !Instant(query, "from", DateTimeOffset.UnixEpoch, out var from))
        {
            return new Validation("from and to must be RFC 3339 instants from 1970 to 9998");
        }

        // Checked before rounding, which could turn a reversed or empty period into a bucket.
        if (query.ContainsKey("from") && from >= to)
        {
            return new Validation("from must be before to, at most 840 hours or 400 days earlier");
        }

        var ticks = granularity.Minutes * TimeSpan.TicksPerMinute;
        var end = ((to - DateTimeOffset.UnixEpoch).Ticks + ticks - 1) / ticks;
        var first = query.ContainsKey("from") ? (from - DateTimeOffset.UnixEpoch).Ticks / ticks : end - (granularity == Hour ? 24 : 30);
        return end - first is >= 1 and var count && count <= granularity.Kept
            ? new UsageWindow(granularity, first, (int)count)
            : new Validation("from must be before to, at most 840 hours or 400 days earlier");
    }

    private static bool Instant(IQueryCollection query, string name, DateTimeOffset fallback, out DateTimeOffset value)
    {
        value = fallback;
        // The last bucket's end must still be a DateTimeOffset.
        return !query.TryGetValue(name, out var raw)
            || (DateTimeOffset.TryParseExact(raw, Rfc3339, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value)
                && value >= DateTimeOffset.UnixEpoch && value < Latest);
    }
}
