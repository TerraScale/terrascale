using System.Net;
using System.Text.Json;

namespace TerraBase.Testing;

internal static class SqlApi
{
    public static string Route(DatabaseApi api) => api.DatabasePath() + "/sql/execute";

    public static Task<ApiResponse> ExecuteAsync(DatabaseApi api, params string[] sql) => ExecuteAsync(api, api.Client, sql);

    public static Task<ApiResponse> ExecuteAsync(DatabaseApi api, HttpClient client, params string[] sql) =>
        SendAsync(api, client, [.. sql.Select(text => (object)new { sql = text })]);

    public static Task<ApiResponse> SendAsync(DatabaseApi api, HttpClient client, params object[] statements) =>
        DatabaseApi.SendAsync(client, HttpMethod.Post, Route(api), JsonContent.Create(new { statements }));

    public static async Task<JsonElement[]> OkAsync(DatabaseApi api, params string[] sql)
    {
        var response = await ExecuteAsync(api, sql);
        response.Status.ShouldBe(HttpStatusCode.OK);
        return [.. response.Json.GetProperty("results").EnumerateArray()];
    }

    public static (HttpStatusCode Status, string? Code, string? Sqlstate, int? Statement) Error(ApiResponse response) =>
        response.Json.TryGetProperty("error", out var error) && error.TryGetProperty("sqlstate", out var sqlstate)
            ? (response.Status, response.Code, sqlstate.GetString(), error.GetProperty("statement").GetInt32())
            : (response.Status, response.Code, null, null);
}
