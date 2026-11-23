namespace TerraScale.Platform.Features.Shapes;

public sealed record ShapeParameters(ShapeOffset Offset, string OffsetText, string? Handle, bool Live, long? Cursor)
{
    public const string Route = "/v1/projects/{project}/databases/{database}/tables/{table}/shape";
    public const string RowRoute = "/v1/projects/{project}/databases/{database}/tables/{table}/rows/{key}";

    private static readonly Validation BadOffset = new("offset must be -1, snap.<S>.<P> or a decimal commit boundary");

    public static Validation? Only(IQueryCollection query, params ReadOnlySpan<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(query);
        foreach (var key in query.Keys)
        {
            if (!allowed.Contains(key))
            {
                return new Validation($"unknown query parameter {key}; accepted: {string.Join(", ", allowed.ToArray())}");
            }
        }

        return null;
    }

    public static string? Parameter(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;

    public static Validation? CheckHandle(string? handle) =>
        handle is not null && !ShapeQuery.IsHandle(handle) ? new Validation("handle must be h1- and 16 lowercase hex characters") : null;

    public static Result<ShapeParameters, Validation> Parse(IQueryCollection query)
    {
        if (Only(query, "handle", "offset", "live", "cursor", "local", "where", "columns") is { } unknown)
        {
            return unknown;
        }

        var offsetText = Parameter(query, "offset");
        if (offsetText is null || ShapeOffset.Parse(offsetText) is not { } offset)
        {
            return BadOffset;
        }

        var liveText = Parameter(query, "live");
        var live = liveText is not null;
        if (live && (liveText != "true" || offset is not DecimalOffset))
        {
            return new Validation("live requires a decimal offset and live=true");
        }

        var handle = Parameter(query, "handle");
        if (handle is null && offset is not BootstrapOffset)
        {
            return new Validation("handle is required unless offset is -1");
        }

        if (CheckHandle(handle) is { } badHandle)
        {
            return badHandle;
        }

        long? cursor = null;
        if (Parameter(query, "cursor") is { } cursorText)
        {
            if (!live)
            {
                return new Validation("cursor requires live=true");
            }

            if (ShapeOffset.ParseDecimal(cursorText) is not { } bucket || bucket == long.MaxValue)
            {
                return new Validation("cursor must be a decimal live bucket below 9223372036854775807");
            }

            cursor = bucket;
        }

        if (Parameter(query, "local") is { } local && (!live || !(local == "0" || ShapeQuery.IsHandle("h1-" + local))))
        {
            return new Validation("local requires live=true and must be 0 or 16 lowercase hex characters");
        }

        return new ShapeParameters(offset, offsetText, handle, live, cursor);
    }
}
