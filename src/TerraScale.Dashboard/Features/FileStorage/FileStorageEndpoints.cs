using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Platform.Clients;

namespace TerraScale.Dashboard.Features.FileStorage
{
    public sealed record FileStoragePage(ShellModel Shell, FileStorageListing? Listing, DateTimeOffset Now, string? Error = null, FileStorageDraft? Draft = null, bool Settings = false, string Prefix = "");
    public sealed record FileStorageDraft(string Dialog, string Error, string Name, TerraBaseFileRules Rules);
    public sealed record FileConfirmDialogModel(string Id, string Action, string Title, string Consequence, string Button, string CsrfToken,
        IReadOnlyDictionary<string, string> Fields, string? Error = null, bool Patch = false);
    public sealed record FileRulesFormModel(string Prefix, TerraBaseFileRules Rules, bool Existing = false);
    public sealed record BucketFilesCardModel(FileStoragePage Page, IReadOnlyList<TerraBaseStoredFile> Visible, string? Next)
    {
        public static BucketFilesCardModel For(FileStoragePage page)
        {
            var files = page.Listing?.Files;
            return new(page, files is null ? [] : FileStorageFormat.Filtered(files, page.Prefix), files?.Next);
        }
    }
    public sealed record BucketSideModel(FileStoragePage Page, IReadOnlyList<TerraBaseStoredFile>? Files, string? Next)
    {
        public static BucketSideModel For(FileStoragePage page)
        {
            var files = page.Listing?.Files;
            return new(page, files is null ? null : FileStorageFormat.Filtered(files, page.Prefix), files?.Next);
        }
    }

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
                return TypedResults.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/storage", TypedResults.BadRequest("Invalid form token."));
            }

            var bucket = form["bucket"].ToString();
            var file = form["fileId"].ToString();
            try
            {
                switch (operation)
                {
                    case "bucket":
                        var createdBucket = await files.CreateBucketAsync(context, form["name"].ToString(), Rules(form), http.RequestAborted);
                        var createdId = createdBucket switch
                        {
                            TerraBaseFileBucket created => created.Id,
                            DashboardForbidden => null,
                        };
                        if (createdId is null)
                        {
                            return await Refused(StatusCodes.Status403Forbidden, Message(403, operation));
                        }

                        bucket = createdId;
                        break;
                    case "delete-bucket":
                        var deletedBucket = await files.DeleteBucketAsync(context, bucket, http.RequestAborted);
                        IResult? deleteBucketRefusal = deletedBucket switch
                        {
                            Done => null,
                            DashboardForbidden => await Refused(StatusCodes.Status403Forbidden, Message(403, operation)),
                            DashboardValidation failure => await Refused(StatusCodes.Status422UnprocessableEntity, failure.Message),
                        };
                        if (deleteBucketRefusal is not null)
                        {
                            return deleteBucketRefusal;
                        }

                        bucket = "";
                        break;
                    case "rules":
                        var rulesSet = await files.SetRulesAsync(context, bucket, Rules(form), http.RequestAborted);
                        IResult? rulesRefusal = rulesSet switch
                        {
                            Done => null,
                            DashboardForbidden => await Refused(StatusCodes.Status403Forbidden, Message(403, operation)),
                        };
                        if (rulesRefusal is not null)
                        {
                            return rulesRefusal;
                        }

                        if (http.Request.IsDatastarRequest)
                        {
                            return await PageAsync(http, context, files, bucket: Empty(bucket), settings: true);
                        }
                        return HttpRedirects.SeeOther(BucketPath(bucket) + "&tab=settings");
                    case "upload":
                        using (var metadata = JsonDocument.Parse(form["metadata"].ToString()))
                        {
                            if (!long.TryParse(form["size"], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                            {
                                throw new ArgumentException("Invalid file size.");
                            }

                            var uploaded = await files.UploadAsync(context, bucket, new(form["name"].ToString(), form["contentType"].ToString(), size, form["sha256"].ToString(), metadata.RootElement.Clone()), http.RequestAborted);
                            return uploaded switch
                            {
                                TerraBaseFileUrl upload => TypedResults.Json(upload, ClientJson.Default.TerraBaseFileUrl),
                                DashboardForbidden => await Refused(StatusCodes.Status403Forbidden, Message(403, operation)),
                            };
                        }
                    case "complete":
                        var completed = await files.CompleteAsync(context, bucket, file, http.RequestAborted);
                        return completed switch
                        {
                            Done => TypedResults.NoContent(),
                            DashboardForbidden => await Refused(StatusCodes.Status403Forbidden, Message(403, operation)),
                        };
                    case "delete":
                        var deletedFile = await files.DeleteAsync(context, bucket, file, http.RequestAborted);
                        IResult? deleteFileRefusal = deletedFile switch
                        {
                            Done => null,
                            DashboardForbidden => await Refused(StatusCodes.Status403Forbidden, Message(403, operation)),
                        };
                        if (deleteFileRefusal is not null)
                        {
                            return deleteFileRefusal;
                        }

                        if (http.Request.IsDatastarRequest)
                        {
                            return await PageAsync(http, context, files, bucket: Empty(bucket), settings: false);
                        }
                        break;
                    case "download":
                        var signed = await files.DownloadAsync(context, bucket, file, http.RequestAborted);
                        http.Response.StatusCode = StatusCodes.Status303SeeOther;
                        http.Response.Headers.Location = signed.Url;
                        http.Response.Headers.CacheControl = "no-store";
                        return TypedResults.Empty;
                }
                return HttpRedirects.SeeOther(bucket.Length == 0 ? "/storage" : BucketPath(bucket));
            }
            catch (Exception error) when (Known(error, http))
            {
                return await Refused(Status(error), Message(error, operation));
            }

            async Task<IResult> Refused(int status, string message)
            {
                if (operation is "upload" or "complete")
                {
                    return TypedResults.Problem(message, statusCode: status);
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
                if (http.Request.IsDatastarRequest && operation is "rules" or "delete")
                {
                    return await PageAsync(http, context, files, message, draft, Empty(bucket), operation is "rules");
                }
                return await PageAsync(http, context, files, draft is null ? message : null, draft, operation is "bucket" ? null : Empty(bucket), operation is "rules" or "delete-bucket");
            }
        }

        private static async Task<IResult> PageAsync(HttpContext http, AccountContext context, FileStorageService files, string? error = null,
            FileStorageDraft? draft = null, string? bucket = null, bool? settings = null)
        {
            FileStorageListing? listing = null;
            var bucketAfter = http.Request.Query["bucketAfter"].ToString();
            string prefix = http.Request.Query["prefix"].ToString();
            if (http.Request.HasFormContentType)
            {
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                bucketAfter = form["bucketAfter"].ToString();
                prefix = form["prefix"].ToString();
            }

            var selected = http.Request.HasFormContentType ? bucket : Empty(http.Request.Query["bucket"].ToString());
            try
            {
                if (selected is null)
                {
                    listing = await files.ListBucketsAsync(context, Empty(bucketAfter), http.RequestAborted);
                }
                else
                {
                    var listed = await files.ListAsync(context, selected, Empty(http.Request.Query["after"].ToString()), Empty(bucketAfter), http.RequestAborted);
                    (listing, var invalid) = listed switch
                    {
                        FileStorageListing found => (found, (DashboardValidation?)null),
                        DashboardValidation failure => (listing, failure),
                    };
                    if (invalid is not null)
                    {
                        error ??= invalid.Message;
                        http.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                    }
                }
            }
            catch (Exception failure) when (Known(failure, http)) { error ??= Message(failure, "list"); http.Response.StatusCode = Status(failure); }
            var page = new FileStoragePage(ShellModel.For(http, context, "File storage"), listing, DateTimeOffset.UtcNow, error, draft,
                settings ?? http.Request.Query["tab"] == "settings", prefix);
            if (http.Request.IsDatastarRequest && listing?.BucketId is not null)
            {
                http.Response.StatusCode = StatusCodes.Status200OK;
                await DatastarEvents.PatchElementsAsync(http, FileStorageBody.Create(page));
                return TypedResults.Empty;
            }
            var view = FileStorageView.Create(page);
            view.StatusCode = http.Response.StatusCode;
            return view;
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
            return new(Empty(form["owner"].ToString()), Policy(form, "read"), Policy(form, "write"));
        }

        private static TerraBaseFilePolicy Policy(IFormCollection form, string key)
        {
            var owner = form[key + "Owner"] == "1";
            var all = form[key + "Public"] == "1";
            return form[key + "Preset"].ToString() switch
            {
                "project" => new(false, false, Roles(form[key + "Roles"].ToString())),
                "owner" => new(true, false, Roles(form[key + "Roles"].ToString())),
                "all" => new(false, true, Roles(form[key + "Roles"].ToString())),
                _ => new(owner, all, Roles(form[key + "Roles"].ToString()))
            };
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
            return error is AccountAccessException { StatusCode: 422 } denied ? denied.Message : Message(Status(error), operation);
        }

        private static string Message(int status, string operation)
        {
            return (status, operation) switch
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
