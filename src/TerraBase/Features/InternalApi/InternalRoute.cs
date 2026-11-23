namespace TerraBase.Features.InternalApi;

internal static class InternalRoute
{
    public const string Root = "/v1/internal";

    public const string Database = Root + "/databases/{databaseId}";

    public const string Tag = "Internal";

    public static Ulid? DatabaseId(string? raw) => raw is { Length: 26 } && Ulid.TryParse(raw, out var id) ? id : null;
}
