using System.Globalization;

namespace TerraBase.Engine;

public static class StorageFormat
{
    public const int Version = 24;

    internal const string Header = "TerraBase-Storage-Format";

    public static string Id { get; } = Version.ToString(CultureInfo.InvariantCulture);
}
