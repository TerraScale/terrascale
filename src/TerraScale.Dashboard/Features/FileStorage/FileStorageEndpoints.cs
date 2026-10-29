using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Shared.UI;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.FileStorage;

public sealed record FileStoragePage(ShellModel Shell, FileStorageListing? Listing, string? Error = null);
public sealed record FileRulesFormModel(string Prefix, TerraBaseFileRules Rules, bool Existing = false);

public static class FileStorageEndpoints
{
    public static IServiceCollection AddFileStorage(this IServiceCollection services) => services.AddSingleton<FileStorageService>();
    public static IEndpointRouteBuilder MapFileStorage(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/storage", ["GET", "HEAD"], async (HttpContext http, DashboardSessions sessions, AccountService accounts, FileStorageService files) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            return await PageAsync(http, context, files);
        });
        foreach (var action in new[] { "bucket", "rules", "delete-bucket", "upload", "complete", "delete", "download" })
        {
            var operation = action;
            endpoints.MapPost("/api/v1/dashboard/storage/" + action, async (HttpContext http, DashboardSessions sessions, AccountService accounts, FileStorageService files) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null) return Results.Unauthorized();
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
                var bucket = form["bucket"].ToString();
                var file = form["fileId"].ToString();
                try
                {
                    switch (operation)
                    {
                        case "bucket": await files.CreateBucketAsync(context, form["name"].ToString(), Rules(form), http.RequestAborted); break;
                        case "rules": await files.SetRulesAsync(context, bucket, Rules(form), http.RequestAborted); break;
                        case "delete-bucket": await files.DeleteBucketAsync(context, bucket, form["confirmName"].ToString(), http.RequestAborted); bucket = ""; break;
                        case "upload":
                            using (var metadata = JsonDocument.Parse(form["metadata"].ToString()))
                            {
                                if (!long.TryParse(form["size"], NumberStyles.None, CultureInfo.InvariantCulture, out var size)) throw new ArgumentException("Invalid file size.");
                                return Results.Json(await files.UploadAsync(context, bucket, new(form["name"].ToString(), form["contentType"].ToString(), size, form["sha256"].ToString(), metadata.RootElement.Clone()), http.RequestAborted));
                            }
                        case "complete": await files.CompleteAsync(context, bucket, file, http.RequestAborted); return Results.NoContent();
                        case "delete": await files.DeleteAsync(context, bucket, file, http.RequestAborted); break;
                        case "download":
                            var signed = await files.DownloadAsync(context, bucket, file, http.RequestAborted);
                            // Avoid redirect result logging: the Location is a private bearer capability.
                            http.Response.StatusCode = StatusCodes.Status303SeeOther;
                            http.Response.Headers.Location = signed.Url;
                            http.Response.Headers.CacheControl = "no-store";
                            return Results.Empty;
                    }
                    return HttpRedirects.SeeOther("/storage" + (bucket.Length == 0 ? "" : "?bucket=" + Uri.EscapeDataString(bucket)));
                }
                catch (Exception error) when (Known(error, http))
                {
                    var status = Status(error);
                    if (operation is "upload" or "complete") return Results.Problem(Message(error), statusCode: status);
                    http.Response.StatusCode = status;
                    return await PageAsync(http, context, files, Message(error), bucket);
                }
            });
        }
        return endpoints;
    }

    private static async Task<IResult> PageAsync(HttpContext http, AccountContext context, FileStorageService files, string? error = null, string? bucket = null)
    {
        FileStorageListing? listing = null;
        var bucketAfter = http.Request.Query["bucketAfter"].ToString();
        if (http.Request.HasFormContentType) bucketAfter = (await http.Request.ReadFormAsync(http.RequestAborted))["bucketAfter"].ToString();
        try { listing = await files.ListAsync(context, Empty(bucket ?? http.Request.Query["bucket"].ToString()), Empty(http.Request.Query["after"].ToString()), Empty(bucketAfter), http.RequestAborted); }
        catch (Exception failure) when (Known(failure, http)) { error ??= Message(failure); http.Response.StatusCode = Status(failure); }
        var page = FileStorageView.Create(new(ShellModel.For(http, context, "File storage"), listing, error));
        page.StatusCode = http.Response.StatusCode;
        return page;
    }
    private static string? Empty(string value) => value.Length == 0 ? null : value;
    private static TerraBaseFileRules Rules(IFormCollection form) => new(Empty(form["owner"].ToString()),
        new(form["readOwner"] == "1", form["readPublic"] == "1", Roles(form["readRoles"].ToString())),
        new(form["writeOwner"] == "1", form["writePublic"] == "1", Roles(form["writeRoles"].ToString())));
    private static string[] Roles(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static bool Known(Exception error, HttpContext http) => error is AccountAccessException or HttpRequestException or IOException or JsonException or ArgumentException or TimeoutException
        || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested;
    private static int Status(Exception error) => error switch
    {
        AccountAccessException denied => denied.StatusCode,
        HttpRequestException { StatusCode: { } status } => (int)status,
        ArgumentException or JsonException => 422,
        _ => 503
    };
    private static string Message(Exception error) => error is AccountAccessException { StatusCode: 422 } denied ? denied.Message : Status(error) switch
    {
        401 => "Sign in again to access project files.",
        403 => "Your role cannot perform this file operation.",
        404 => "The bucket or file is unavailable in this project.",
        409 => "The file is incomplete or the bucket still contains files. Wait for cleanup before deleting a bucket.",
        429 => "The shared database and file storage allowance is full.",
        400 or 422 => "Check the bucket name, access rules, file metadata and upload details.",
        _ => "File storage is temporarily unavailable. Try again."
    };
}
