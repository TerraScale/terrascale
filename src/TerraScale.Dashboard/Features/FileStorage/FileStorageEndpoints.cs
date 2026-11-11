using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Common.UI;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.FileStorage;

namespace TerraScale.Dashboard.Features.FileStorage
{
    public sealed record FileStoragePage(ShellModel Shell, FileStorageListing? Listing, DateTimeOffset Now, string? Error = null, FileStorageDraft? Draft = null, bool Settings = false);
    public sealed record FileStorageDraft(string Dialog, string Error, string Name, TerraBaseFileRules Rules);
    public sealed record FileConfirmDialogModel(string Id, string Action, string Title, string Consequence, string Button, string CsrfToken,
        IReadOnlyDictionary<string, string> Fields, string? Error = null);
    public sealed record FileRulesFormModel(string Prefix, TerraBaseFileRules Rules, bool Existing = false);

    public static class FileStorageEndpoints
    {
        public static IServiceCollection AddFileStorage(this IServiceCollection services)
        {
            return services.AddSingleton<FileStorageService>();
        }

        internal static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, FileStorageService files)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            return await PageAsync(http, context, files);
        }

        internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, FileStorageService files, string operation)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/storage", Results.BadRequest("Invalid form token."));
            }

            var bucket = form["bucket"].ToString();
            var file = form["fileId"].ToString();
            try
            {
                switch (operation)
                {
                    case "bucket": bucket = (await files.CreateBucketAsync(context, form["name"].ToString(), Rules(form), http.RequestAborted)).Id; break;
                    case "delete-bucket": await files.DeleteBucketAsync(context, bucket, http.RequestAborted); bucket = ""; break;
                    case "rules": await files.SetRulesAsync(context, bucket, Rules(form), http.RequestAborted); return HttpRedirects.SeeOther(BucketPath(bucket) + "&tab=settings");
                    case "upload":
                        using (var metadata = JsonDocument.Parse(form["metadata"].ToString()))
                        {
                            if (!long.TryParse(form["size"], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                            {
                                throw new ArgumentException("Invalid file size.");
                            }

                            return Results.Json(await files.UploadAsync(context, bucket, new(form["name"].ToString(), form["contentType"].ToString(), size, form["sha256"].ToString(), metadata.RootElement.Clone()), http.RequestAborted), FileStorageJson.Default.TerraBaseFileUrl);
                        }
                    case "complete": await files.CompleteAsync(context, bucket, file, http.RequestAborted); return Results.NoContent();
                    case "delete": await files.DeleteAsync(context, bucket, file, http.RequestAborted); break;
                    case "download":
                        var signed = await files.DownloadAsync(context, bucket, file, http.RequestAborted);
                        http.Response.StatusCode = StatusCodes.Status303SeeOther;
                        http.Response.Headers.Location = signed.Url;
                        http.Response.Headers.CacheControl = "no-store";
                        return Results.Empty;
                }
                return HttpRedirects.SeeOther(bucket.Length == 0 ? "/storage" : BucketPath(bucket));
            }
            catch (Exception error) when (Known(error, http))
            {
                var status = Status(error);
                var message = Message(error, operation);
                if (operation is "upload" or "complete")
                {
                    return Results.Problem(message, statusCode: status);
                }

                http.Response.StatusCode = status;
                FileStorageDraft? draft = operation switch
                {
                    "bucket" => new("create-bucket", message, form["name"].ToString(), Rules(form)),
                    "rules" => new("", message, "", Rules(form)),
                    "delete-bucket" => new("delete-bucket", message, "", Rules(form)),
                    "delete" => new("delete-file-" + file, message, "", Rules(form)),
                    _ => null
                };
                return await PageAsync(http, context, files, draft is null ? message : null, draft, operation is "bucket" ? null : Empty(bucket), operation is "rules" or "delete-bucket");
            }
        }

        private static async Task<IResult> PageAsync(HttpContext http, AccountContext context, FileStorageService files, string? error = null,
            FileStorageDraft? draft = null, string? bucket = null, bool? settings = null)
        {
            FileStorageListing? listing = null;
            var bucketAfter = http.Request.Query["bucketAfter"].ToString();
            if (http.Request.HasFormContentType)
            {
                bucketAfter = (await http.Request.ReadFormAsync(http.RequestAborted))["bucketAfter"].ToString();
            }

            var selected = http.Request.HasFormContentType ? bucket : Empty(http.Request.Query["bucket"].ToString());
            try
            {
                listing = selected is null
                    ? await files.ListBucketsAsync(context, Empty(bucketAfter), http.RequestAborted)
                    : await files.ListAsync(context, selected, Empty(http.Request.Query["after"].ToString()), Empty(bucketAfter), http.RequestAborted);
            }
            catch (Exception failure) when (Known(failure, http)) { error ??= Message(failure, "list"); http.Response.StatusCode = Status(failure); }
            var page = FileStorageView.Create(new(ShellModel.For(http, context, "File storage"), listing, DateTimeOffset.UtcNow, error, draft,
                settings ?? http.Request.Query["tab"] == "settings"));
            page.StatusCode = http.Response.StatusCode;
            return page;
        }

        internal static string BucketPath(string bucket)
        {
            return "/storage?bucket=" + Uri.EscapeDataString(bucket);
        }
        private static string? Empty(string value)
        {
            return value.Length == 0 ? null : value;
        }

        private static TerraBaseFileRules Rules(IFormCollection form)
        {
            return new(Empty(form["owner"].ToString()),
                new(form["readOwner"] == "1", form["readPublic"] == "1", Roles(form["readRoles"].ToString())),
                new(form["writeOwner"] == "1", form["writePublic"] == "1", Roles(form["writeRoles"].ToString())));
        }

        private static string[] Roles(string value)
        {
            return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool Known(Exception error, HttpContext http)
        {
            return error is AccountAccessException or TerraBaseException or HttpRequestException or IOException or JsonException or ArgumentException or TimeoutException
                || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested;
        }

        private static int Status(Exception error)
        {
            return error switch
            {
                AccountAccessException denied => denied.StatusCode,
                TerraBaseException failure => failure.Status,
                HttpRequestException { StatusCode: { } status } => (int)status,
                ArgumentException or JsonException => 422,
                _ => 503
            };
        }

        private static string Message(Exception error, string operation)
        {
            return error is AccountAccessException { StatusCode: 422 } denied ? denied.Message : (Status(error), operation) switch
            {
                (401, _) => "Sign in again to access project files.",
                (403, _) => "Your role cannot perform this file operation.",
                (404, _) => "The bucket or file is unavailable in this project.",
                (409, "bucket") => "This project already has 100 buckets.",
                (409, "delete-bucket") => "The bucket still has files, or files deleted in the last 6 minutes. Try again later.",
                (409, _) => "The file is incomplete. Reload the bucket and try again.",
                (429, _) => "The shared database and file storage allowance is full.",
                (400 or 422, "bucket") => "Check the bucket name and access rules. Owner access needs an owner field.",
                (400 or 422, "rules") => "Check the access rules. Owner access needs an owner field.",
                (400 or 422, _) => "Check the file and its metadata.",
                _ => "File storage is temporarily unavailable. Try again."
            };
        }
    }
}
