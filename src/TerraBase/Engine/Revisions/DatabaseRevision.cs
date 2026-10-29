using System.Globalization;

namespace TerraBase.Engine.Revisions;

internal readonly record struct DatabaseRevision(Ulid Incarnation, ulong Generation, ulong Frame)
{
    private const string Prefix = "ts1:";

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{Incarnation}:{Generation}:{Frame}");

    public static bool TryParse(string? text, out DatabaseRevision revision)
    {
        revision = default;
        if (text is null || text.Length > 96 || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = text.AsSpan(Prefix.Length);
        Span<Range> parts = stackalloc Range[4];
        if (body.Split(parts, ':') != 3)
        {
            return false;
        }

        var incarnationText = body[parts[0]];
        if (!Ulid.TryParse(incarnationText, out var incarnation)
            || !incarnationText.SequenceEqual(incarnation.ToString())
            || !ulong.TryParse(body[parts[1]], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || !ulong.TryParse(body[parts[2]], NumberStyles.None, CultureInfo.InvariantCulture, out var frame)
            || generation == 0)
        {
            return false;
        }

        revision = new DatabaseRevision(incarnation, generation, frame);
        return true;
    }
}
