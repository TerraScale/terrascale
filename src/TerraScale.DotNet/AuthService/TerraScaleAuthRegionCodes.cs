namespace TerraScale.DotNet;

/// <summary>The regional identity grammar published by Atlas auth discovery.</summary>
internal static class TerraScaleAuthRegionCodes
{
    internal static bool IsValid(string value) =>
        value.Length is >= 1 and <= 16 &&
        (value[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) &&
        value.All(character => character is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-');
}
