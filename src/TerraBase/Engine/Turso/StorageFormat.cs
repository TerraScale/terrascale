using System.Globalization;

namespace TerraBase.Engine;

public static class StorageFormat
{
    public const int SupportedFrom = 23;

    public const int SupportedTo = 24;

    public const int Target = 23;

    internal const string Header = "TerraBase-Storage-Format";

    internal const string AcceptHeader = "TerraBase-Storage-Formats";

    private const int First = 23;

    private const string FirstName = "23-41a88901325a";

    private const int FirstFileVersion = 1;

    public static string Id { get; } = Name(Target);

    internal static string Supported { get; } = $"{SupportedFrom}-{SupportedTo}";

    internal static bool IsSupported(int format) => format is >= SupportedFrom and <= SupportedTo;

    internal static string Name(int format) => format == First ? FirstName : format.ToString(CultureInfo.InvariantCulture);

    internal static string? Marker(int format) => format == First ? null : Name(format);

    internal static int? Parse(string name) => name switch
    {
        FirstName => First,
        _ when int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var format) && format > First => format,
        _ => null,
    };

    internal static int? FromMarker(string? marker) => marker is null ? First : Parse(marker);

    internal static bool Accepts(string? supported, int format)
    {
        if (supported is null)
        {
            return format == First;
        }

        var bounds = supported.Split('-');
        return bounds.Length == 2
            && int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from)
            && int.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out var to)
            && format >= from && format <= to;
    }

    internal static int OfFile(int fileVersion) => fileVersion == FirstFileVersion ? First : fileVersion;

    internal static int FileVersion(int format) => format == First ? FirstFileVersion : format;

    internal static string Refusal(string subject, int format) => format > SupportedTo
        ? $"{subject} has storage format {Name(format)}, newer than this build, which supports {SupportedFrom} to {SupportedTo}; run a build that supports {Name(format)}"
        : $"{subject} has storage format {Name(format)}, older than this build, which supports {SupportedFrom} to {SupportedTo}; open it with a build that supports {Name(format)} and migrate it forward";
}
