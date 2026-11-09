namespace TerraBase.Engine;

internal static class StoredDataCorruption
{
    private const string Marker = "terrabase.stored-data-corrupt";

    public static InvalidDataException Create(string message, Exception? inner = null)
    {
        var error = new InvalidDataException(message, inner);
        error.Data[Marker] = true;
        return error;
    }

    public static bool Is(Exception error) => error is InvalidDataException && error.Data[Marker] is true;
}
