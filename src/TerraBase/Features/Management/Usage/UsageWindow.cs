using System.Globalization;
using TerraBase.Errors;

namespace TerraBase.Features.Management.Usage;

public sealed record UsageGranularity(string Name, int Hours, int Kept, string Format)
{
    public int Minutes { get; init; } = Hours * 60;

    public string Kind => "usage-" + Name + "s";

    public DateTimeOffset Start(long bucket) => DateTimeOffset.UnixEpoch.AddMinutes(bucket * Minutes);

    public string Id(long bucket) => Start(bucket).ToString(Format, CultureInfo.InvariantCulture);

    public long Parse(string id) =>
        DateTimeOffset.ParseExact(id, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds() / 60 / Minutes;
}

public sealed record UsageWindow(UsageGranularity Granularity, long First, int Count)
{
    public static UsageGranularity Minute { get; } = new("minute", 0, 120, "yyyy-MM-dd'T'HH:mm") { Minutes = 1 };

    public static UsageGranularity Hour { get; } = new("hour", 1, 35 * 24, "yyyy-MM-dd'T'HH");

    public static UsageGranularity Day { get; } = new("day", 24, 400, "yyyy-MM-dd");

    private static readonly DateTimeOffset Latest = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // RFC 3339 date-times: a full date and time, optional fraction, and Z or a numeric offset.
    private static readonly string[] Rfc3339 =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    public static IReadOnlyList<UsageGranularity> Granularities { get; } = [Minute, Hour, Day];

    public const string Description =
        "Query: granularity (hour, the default, or day: UTC buckets), from (inclusive, rounded down to its bucket) and to (exclusive, "
        + "rounded up; default now), as RFC 3339 instants. Without from, the period is the 24 hours or 30 days that end with to. "
        + "A period holds at most 840 hourly or 400 daily buckets, which is how long they are kept.";

    public DateTimeOffset From => Granularity.Start(First);

    public DateTimeOffset To => Granularity.Start(First + Count);

    public int? IndexOf(long hour, int minute = 0) => ((hour * 60 + minute) / Granularity.Minutes) - First is var index && index >= 0 && index < Count ? (int)index : null;

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
        return !query.TryGetValue(name, out var raw)
            || (DateTimeOffset.TryParseExact(raw, Rfc3339, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value)
                && value >= DateTimeOffset.UnixEpoch && value < Latest);
    }
}
