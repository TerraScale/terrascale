using System.Net;
using System.Text.Json;

namespace TerraBase.Testing;

/// <summary>Sends statements to <c>…/sql/execute</c> and reads results and errors.</summary>
internal static class SqlApi
{
    public static string Route(DatabaseApi api) => api.DatabasePath() + "/sql/execute";

    /// <summary>Runs <paramref name="sql"/> statements without arguments as one request.</summary>
    public static Task<ApiResponse> ExecuteAsync(DatabaseApi api, params string[] sql) => ExecuteAsync(api, api.Client, sql);

    public static Task<ApiResponse> ExecuteAsync(DatabaseApi api, HttpClient client, params string[] sql) =>
        SendAsync(api, client, [.. sql.Select(text => (object)new { sql = text })]);

    /// <summary>Runs statements given as request objects (<c>{sql, args, named}</c>).</summary>
    public static Task<ApiResponse> SendAsync(DatabaseApi api, HttpClient client, params object[] statements) =>
        DatabaseApi.SendAsync(client, HttpMethod.Post, Route(api), JsonContent.Create(new { statements }));

    /// <summary>Runs statements that must succeed and returns their results.</summary>
    public static async Task<JsonElement[]> OkAsync(DatabaseApi api, params string[] sql)
    {
        var response = await ExecuteAsync(api, sql);
        response.Status.ShouldBe(HttpStatusCode.OK);
        return [.. response.Json.GetProperty("results").EnumerateArray()];
    }

    /// <summary>The SQLSTATE of a refused request, with its statement index.</summary>
    public static (HttpStatusCode Status, string? Code, string? Sqlstate, int? Statement) Error(ApiResponse response) =>
        response.Json.TryGetProperty("error", out var error) && error.TryGetProperty("sqlstate", out var sqlstate)
            ? (response.Status, response.Code, sqlstate.GetString(), error.GetProperty("statement").GetInt32())
            : (response.Status, response.Code, null, null);
}
