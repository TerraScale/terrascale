using System.Buffers;
using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// Builds TerraBase geo filter nodes for <see cref="TerraBaseQuery.Filter"/>. Points are objects with numeric <c>lat</c> and
/// <c>lon</c> members (WGS 84 degrees), for example <c>{"loc":{"lat":-23.55,"lon":-46.63}}</c>. A <c>within</c> node nests
/// under <c>and</c>, <c>or</c> and <c>not</c>, and works in counts, aggregates and transaction assertions; a document without
/// a numeric point is not within.
/// </summary>
public static class TerraBaseFilter
{
    private const double MaxMeters = 20_000_000;

    /// <summary>
    /// <c>{"within":[path,{"lat","lon","meters"}]}</c>: points at most <paramref name="meters"/> from the center, by haversine
    /// distance on the WGS 84 mean radius.
    /// </summary>
    /// <param name="path">JSON Pointer of the point member.</param>
    /// <param name="lat">Center latitude, -90 to 90.</param>
    /// <param name="lon">Center longitude, -180 to 180.</param>
    /// <param name="meters">Radius, greater than 0 and at most 20000000.</param>
    public static JsonElement WithinRadius(string path, double lat, double lon, double meters)
    {
        RequirePoint(path, lat, lon);
        if (meters is not (> 0 and <= MaxMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(meters), meters, "meters must be greater than 0 and at most 20000000.");
        }

        return Within(path, [("lat", lat), ("lon", lon), ("meters", meters)]);
    }

    /// <summary>
    /// <c>{"within":[path,{"south","west","north","east"}]}</c>: points in a latitude and longitude box; <paramref name="west"/>
    /// greater than <paramref name="east"/> crosses the antimeridian.
    /// </summary>
    /// <param name="path">JSON Pointer of the point member.</param>
    /// <param name="south">Least latitude, -90 to 90.</param>
    /// <param name="west">Western longitude, -180 to 180.</param>
    /// <param name="north">Greatest latitude, at least <paramref name="south"/>.</param>
    /// <param name="east">Eastern longitude, -180 to 180.</param>
    public static JsonElement WithinBox(string path, double south, double west, double north, double east)
    {
        RequirePoint(path, south, west);
        RequirePoint(path, north, east);
        if (south > north)
        {
            throw new ArgumentException("south must be at most north.", nameof(south));
        }

        return Within(path, [("south", south), ("west", west), ("north", north), ("east", east)]);
    }

    internal static void RequirePoint(string path, double lat, double lon)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path == "$id")
        {
            throw new ArgumentException("A geo path must be a JSON Pointer.", nameof(path));
        }

        if (lat is not (>= -90 and <= 90) || lon is not (>= -180 and <= 180))
        {
            throw new ArgumentOutOfRangeException(nameof(lat), "lat must be -90 to 90 and lon -180 to 180.");
        }
    }

    private static JsonElement Within(string path, ReadOnlySpan<(string Name, double Value)> area)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("within");
            writer.WriteStringValue(path);
            writer.WriteStartObject();
            foreach (var (name, value) in area)
            {
                writer.WriteNumber(name, value);
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
