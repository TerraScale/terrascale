namespace TerraBase.Errors;

public static class ErrorDocs
{
    private const string BaseUrl = "https://docs.terrascale.tech/guides/errors";

    public static string Url(string code) => $"{BaseUrl}#{code.Replace('_', '-')}";
}
