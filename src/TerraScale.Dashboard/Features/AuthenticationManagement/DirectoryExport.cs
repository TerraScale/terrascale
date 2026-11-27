using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Features.AuthenticationManagement
{
    public static partial class AuthenticationConsole
    {
        internal static async Task<IResult> ExportUsersAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ITsAuthProjects projects)
        {
            http.Response.Headers.CacheControl = "no-store";
            var required = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (!AccountRequest.TryResolveApi(http, required, out var context, out var rejection))
            {
                return rejection;
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/auth/users", TypedResults.StatusCode(403));
            }

            if (!context.CanManageProject || context.Project is null)
            {
                return TypedResults.StatusCode(403);
            }

            if (form["project_id"] != context.Project.Id)
            {
                return TypedResults.Text("The selected project changed. Reload Users before exporting.", statusCode: 409);
            }

            var format = form["format"].ToString();
            var after = form["after"].ToString();
            if (format is not ("json" or "csv") || after.Length > 8192 || after.Any(char.IsControl))
            {
                return TypedResults.Text("Choose JSON or CSV and a valid resume cursor.", statusCode: 422);
            }

            try
            {
                var page = await projects.ExportUsersAsync(context.Session, context.Project.Id, after.Length == 0 ? null : after, http.RequestAborted);
                if (page.Items.Count > 100)
                {
                    throw new IOException("ts-auth returned an oversized directory page.");
                }

                if (page.Next is { Length: > 0 } next)
                {
                    if (next.Length > 8192 || next.Any(character => character is < '!' or > '~'))
                    {
                        throw new IOException("ts-auth returned an invalid directory cursor.");
                    }

                    http.Response.Headers["X-Export-Next"] = next;
                }

                http.Response.Headers["X-Export-Created-Before"] = page.CreatedBefore.ToString("O", CultureInfo.InvariantCulture);
                var export = new DirectoryExportPage([.. page.Items.Select(user => new DirectoryExportUser(user.Id, user.Email, user.EmailVerified, user.CreatedAt, user.Disabled,
                    [.. user.LinkedProviders.Select(provider => new DirectoryExportProvider(provider.Provider, provider.ClientId, provider.Subject))]))], page.Next, page.CreatedBefore);
                var content = format == "json"
                    ? JsonSerializer.SerializeToUtf8Bytes(export, DirectoryExportJson.Default.DirectoryExportPage)
                    : Encoding.UTF8.GetBytes(DirectoryCsv(export));
                return TypedResults.File(content, format == "json" ? "application/json" : "text/csv; charset=utf-8", $"users-{context.Project.Id}.{format}");
            }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            {
                return TypedResults.Text(Unreachable, statusCode: 503);
            }
            catch (Exception error) when (error is AccountAccessException or HttpRequestException or IOException)
            {
                return TypedResults.Text(error is AccountAccessException ? error.Message : Unreachable,
                    statusCode: error is AccountAccessException access ? access.StatusCode : 503);
            }
        }

        private static string DirectoryCsv(DirectoryExportPage page)
        {
            var csv = new StringBuilder("id,email,email_verified,created_at,disabled,linked_providers\r\n");
            foreach (var user in page.Items)
            {
                var providers = JsonSerializer.Serialize([.. user.LinkedProviders], DirectoryExportJson.Default.DirectoryExportProviderArray);
                csv.AppendJoin(',', new[] { user.Id, user.Email, user.EmailVerified ? "true" : "false", user.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                    user.Disabled ? "true" : "false", providers }.Select(CsvCell)).Append("\r\n");
            }
            return csv.ToString();
        }

        private static string CsvCell(string value)
        {
            var trimmed = value.TrimStart();
            if ((trimmed.Length > 0 && "'=+-@".Contains(trimmed[0])) || (value.Length > 0 && char.IsControl(value[0])))
            {
                value = "'" + value;
            }

            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }
    }

    internal sealed record DirectoryExportPage(IReadOnlyList<DirectoryExportUser> Items, string? Next, DateTimeOffset CreatedBefore);
    internal sealed record DirectoryExportUser(string Id, string Email, bool EmailVerified, DateTimeOffset CreatedAt, bool Disabled, IReadOnlyList<DirectoryExportProvider> LinkedProviders);
    internal sealed record DirectoryExportProvider(string Provider, string ClientId, string Subject);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(DirectoryExportPage))]
    [JsonSerializable(typeof(DirectoryExportProvider[]))]
    internal sealed partial class DirectoryExportJson : JsonSerializerContext;
}
