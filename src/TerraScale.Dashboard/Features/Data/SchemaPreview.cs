using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed record SchemaPreviewResult(int Examined, int Valid, int Invalid, IReadOnlyList<SchemaPreviewDiagnostic> Diagnostics,
        string SchemaHash, string Frontier, bool Complete, string? NextCursor);
    public sealed record SchemaPreviewDiagnostic(string IdHash, string PathHash, string Keyword);
    public sealed record SchemaPreviewPage(ShellModel Shell, DatabaseRecord Database, string Collection, string Schema = "{}",
        string Cursor = "", SchemaPreviewResult? Preview = null, string Error = "");

    public sealed class SchemaPreview(IHttpClientFactory factory, IProjectAccessTokens tokens, DatabaseService databases)
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };

        public async Task<SchemaPreviewResult> ReadAsync(AccountContext context, DatabaseRecord database, string collection, string schema, string cursor, CancellationToken cancellationToken)
        {
            if (context.Role != "owner")
            {
                throw new AccountAccessException("Only organization owners can preview document schemas.");
            }

            if (PlatformDatabases.IsPlatform(context.Organization!.Id))
            {
                throw PlatformDatabases.ReadOnly();
            }

            DataService.ValidCollection(collection);
            if (System.Text.Encoding.UTF8.GetByteCount(schema) > 48 * 1024 || cursor.Length > 8192 || cursor.Any(char.IsControl))
            {
                throw new AccountAccessException("Use a schema up to 48 KiB and a valid continuation.", 422);
            }

            JsonDocument parsed;
            try { parsed = JsonDocument.Parse(schema); }
            catch (JsonException) { throw new AccountAccessException("Enter a valid JSON Schema.", 422); }
            using var validSchema = parsed;
            if (parsed.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            {
                throw new AccountAccessException("A JSON Schema must be an object or boolean.", 422);
            }

            var scope = DatabaseService.Scope(context);
            var home = await databases.HomeAsync(context, database, cancellationToken);
            var access = await tokens.GetAsync(scope.Session!, scope.OrganizationId, scope.ProjectId, ["documents:read", "documents:write"], cancellationToken);
            var path = $"/v1/projects/{Uri.EscapeDataString(scope.ProjectId)}/databases/{Uri.EscapeDataString(database.Name)}/collections/{Uri.EscapeDataString(collection)}/schema/preview";
            if (cursor.Length > 0)
            {
                path += "?cursor=" + Uri.EscapeDataString(cursor);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(home.Api, path)) { Content = new StringContent(schema, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            request.Headers.Add("X-TerraScale-Region-Pin", home.Id);
            using var response = await factory.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new AccountAccessException("This preview is stale. Data changed or the continuation expired. Start again; previous progress has been cleared.", 409);
            }

            response.EnsureSuccessStatusCode();
            using var document = await PlatformJson.ReadAsync(response.Content, cancellationToken);
            var result = document.RootElement.Deserialize<SchemaPreviewResult>(Json) ?? throw new JsonException();
            if (result.Examined is < 0 or > 100 || result.Valid < 0 || result.Invalid < 0 || result.Valid + result.Invalid != result.Examined ||
                result.Diagnostics is null || result.Diagnostics.Count > 16 || result.SchemaHash is not { Length: 64 } || !result.SchemaHash.All(char.IsAsciiHexDigit) ||
                result.Frontier is not { Length: 64 } || !result.Frontier.All(char.IsAsciiHexDigit) ||
                result.Complete != string.IsNullOrEmpty(result.NextCursor) || result.NextCursor?.Length > 8192 || result.NextCursor?.Any(char.IsControl) == true ||
                result.Diagnostics.Any(item => item is null || item.IdHash is not { Length: 64 } || !item.IdHash.All(char.IsAsciiHexDigit) ||
                    item.PathHash is not { Length: 64 } || !item.PathHash.All(char.IsAsciiHexDigit) || item.Keyword is not { Length: > 0 and <= 64 } || !item.Keyword.All(char.IsAsciiLetterOrDigit)))
            {
                throw new JsonException("Schema preview response is invalid.");
            }

            return result;
        }
    }

    public static class SchemaPreviewEndpoints
    {
        public static IEndpointRouteBuilder MapSchemaPreview(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapMethods("/databases/{databaseId}/schema/preview", ["GET", "POST"], async Task<IResult> (HttpContext http, string databaseId,
                DashboardSessions sessions, AccountService accounts, SchemaPreview preview) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return AccountRequest.LoginRedirect(http);
                }

                if (context.Role != "owner")
                {
                    return Results.Problem("Only organization owners can preview document schemas.", statusCode: 403);
                }

                var database = DatabaseService.Database(context, databaseId);
                var page = new SchemaPreviewPage(ShellModel.For(http, context, "Schema preview"), database, http.Request.Query["collection"].ToString());
                var status = 200;
                if (HttpMethods.IsPost(http.Request.Method))
                {
                    var form = await http.Request.ReadFormAsync(http.RequestAborted);
                    if (!Csrf.Validate(http, form))
                    {
                        return Results.BadRequest("Invalid form token.");
                    }

                    page = page with { Collection = form["collection"].ToString(), Schema = form["schema"].ToString() };
                    try
                    {
                        var result = await preview.ReadAsync(context, database, page.Collection, page.Schema, form["cursor"].ToString(), http.RequestAborted);
                        page = page with { Preview = result, Cursor = result.NextCursor ?? "" };
                    }
                    catch (Exception error) when (error is JsonException or HttpRequestException or IOException or AccountAccessException)
                    {
                        status = error is JsonException ? 503 : DatabaseResults.Status(error);
                        page = page with { Error = error is JsonException ? "The preview service returned an invalid response. Start again shortly." : DatabaseResults.Message(error) };
                    }
                }
                var view = SchemaPreviewView.Create(page);
                view.StatusCode = status;
                return view;
            });
            return endpoints;
        }
    }
}
