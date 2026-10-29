using System.Net;
using System.Text.Json;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

/// <summary>Sends statements to <c>…/sql/execute</c> and reads results and errors.</summary>
internal static class SqlApi
{
    public static string Route(DocumentApi api) => api.DatabasePath() + "/sql/execute";

    /// <summary>Runs <paramref name="sql"/> statements without arguments as one request.</summary>
    public static Task<ApiResponse> ExecuteAsync(DocumentApi api, params string[] sql) => ExecuteAsync(api, api.Client, sql);

    public static Task<ApiResponse> ExecuteAsync(DocumentApi api, HttpClient client, params string[] sql) =>
        SendAsync(api, client, [.. sql.Select(text => (object)new { sql = text })]);

    /// <summary>Runs statements given as request objects (<c>{sql, args, named}</c>).</summary>
    public static Task<ApiResponse> SendAsync(DocumentApi api, HttpClient client, params object[] statements) =>
        DocumentApi.SendAsync(client, HttpMethod.Post, Route(api), JsonContent.Create(new { statements }));

    /// <summary>Runs statements that must succeed and returns their results.</summary>
    public static async Task<JsonElement[]> OkAsync(DocumentApi api, params string[] sql)
    {
        var response = await ExecuteAsync(api, sql);
        response.Status.ShouldBe(HttpStatusCode.OK);
        return [.. response.Json.GetProperty("results").EnumerateArray()];
    }

    /// <summary>The SQLite code of a refused request, with its statement index.</summary>
    public static (HttpStatusCode Status, string? Code, string? Sqlite, int? Statement) Error(ApiResponse response) =>
        response.Json.TryGetProperty("error", out var error) && error.TryGetProperty("sqlite_code", out var sqlite)
            ? (response.Status, response.Code, sqlite.GetString(), error.GetProperty("statement").GetInt32())
            : (response.Status, response.Code, null, null);
}
