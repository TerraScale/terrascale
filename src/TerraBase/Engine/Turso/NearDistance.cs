using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace TerraBase.Engine.Turso;

/// <summary>
/// The distance of a <see cref="NearSort"/> to one stored document, as the <c>tb_near</c> SQL function: one forward pass
/// over the document bytes, no <c>json_extract</c> copy. Values compare as float32, like Turso's <c>vector32</c>.
/// </summary>
internal sealed class NearDistance(NearSort near)
{
    private readonly float[] _query = [.. near.Vector.Select(static value => (float)value)];
    private readonly byte[][] _path = [.. near.Path.Segments!.Select(Encoding.UTF8.GetBytes)];

    /// <summary>
    /// The distance, or <see cref="double.PositiveInfinity"/> when the path is not an all-numeric array of the query's
    /// length: it sorts after every distance, so a single call per row both orders and excludes.
    /// </summary>
    public double Measure(object? document)
    {
        var json = document switch
        {
            byte[] bytes => bytes,
            string text => Encoding.UTF8.GetBytes(text),
            _ => null,
        };
        if (json is null)
        {
            return double.PositiveInfinity;
        }

        try
        {
            var reader = new Utf8JsonReader(json);
            return !Seek(ref reader, _path) ? double.PositiveInfinity
                : near.Point is { } point ? Geo(ref reader, point) : Distance(ref reader);
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            // Malformed or beyond double range: no distance.
            return double.PositiveInfinity;
        }
    }

    // Leaves the reader on the value at the path; repeated member names take the first.
    internal static bool Seek(ref Utf8JsonReader reader, byte[][] path)
    {
        reader.Read();
        foreach (var segment in path)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (true)
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return false;
                }

                var found = reader.ValueTextEquals(segment);
                reader.Read();
                if (found)
                {
                    break;
                }

                reader.Skip();
            }
        }

        return true;
    }

    // The first numeric lat and lon members of the object at the path.
    private static double Geo(ref Utf8JsonReader reader, GeoPoint point)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return double.PositiveInfinity;
        }

        object? lat = null, lon = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isLat = reader.ValueTextEquals("lat"u8);
            var isLon = reader.ValueTextEquals("lon"u8);
            reader.Read();
            if (reader.TokenType == JsonTokenType.Number && ((isLat && lat is null) || (isLon && lon is null)))
            {
                (isLat ? ref lat : ref lon) = reader.GetDouble();
            }

            reader.Skip();
        }

        return GeoDistance.Meters(lat, lon, point.Lat, point.Lon);
    }

    private double Distance(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return double.PositiveInfinity;
        }

        double dot = 0, documentNorm = 0, queryNorm = 0, squares = 0;
        var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number || count == _query.Length)
            {
                return double.PositiveInfinity;
            }

            var value = (float)reader.GetDouble();
            if (!float.IsFinite(value))
            {
                return double.PositiveInfinity;
            }

            var query = _query[count++];
            dot += value * (double)query;
            documentNorm += value * (double)value;
            queryNorm += query * (double)query;
            squares += (value - (double)query) * (value - (double)query);
        }

        return count != _query.Length ? double.PositiveInfinity : Result(dot, documentNorm, queryNorm, squares);
    }

    /// <summary>The distance to a vector index's stored values: <see cref="TursoStorageTransaction"/> float32, little-endian.</summary>
    public double Measure(ReadOnlySpan<byte> values)
    {
        double dot = 0, documentNorm = 0, queryNorm = 0, squares = 0;
        for (var i = 0; i < _query.Length; i++)
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(values[(4 * i)..]);
            var query = _query[i];
            dot += value * (double)query;
            documentNorm += value * (double)value;
            queryNorm += query * (double)query;
            squares += (value - (double)query) * (value - (double)query);
        }

        return Result(dot, documentNorm, queryNorm, squares);
    }

    private double Result(double dot, double documentNorm, double queryNorm, double squares)
    {
        if (near.Euclidean)
        {
            return Math.Sqrt(squares);
        }

        // A zero document vector has no direction.
        return documentNorm == 0 ? double.PositiveInfinity : 1 - (dot / (Math.Sqrt(documentNorm) * Math.Sqrt(queryNorm)));
    }
}
