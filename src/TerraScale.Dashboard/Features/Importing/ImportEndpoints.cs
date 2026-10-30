using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.TerraBase.Contracts.Import;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.Importing
{
    public static class ImportEndpoints
    {
        private static readonly ImportJson Json = new(new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 256 });

        public static IServiceCollection AddImportFeatures(this IServiceCollection services)
        {
            return services.AddSingleton<ImportService>();
        }

        public static IEndpointRouteBuilder MapImports(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/imports", async (HttpContext http, DashboardSessions sessions, AccountService accounts, ImportService imports) =>
            {
                var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
                return context is null ? AccountEndpoints.LoginRedirect(http) : await ListPageAsync(http, context, imports);
            });
            endpoints.MapGet("/api/v1/dashboard/imports", async (HttpContext http, DashboardSessions sessions, AccountService accounts, ImportService imports) =>
            {
                var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var (jobs, partial) = await imports.JobsAsync(context, http.RequestAborted);
                    return partial ? Results.Problem(Partial, statusCode: 503) : Results.Json(jobs, Json.IReadOnlyListTerraBaseImportJob);
                }
                catch (Exception error) when (KnownFailure(http, error)) { return Failure(error); }
            });
            // The browser fingerprints the export first; then this creates the destination database and attaches the import.
            endpoints.MapPost("/api/v1/dashboard/imports", async (HttpContext http, DashboardSessions sessions, AccountService accounts, ImportService imports) =>
            {
                var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.BadRequest("Invalid form token.");
                }

                try
                {
                    var job = await imports.CreateAsync(context, form["database"].ToString(), form["format"].ToString(),
                        long.TryParse(form["sourceBytes"], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : 0,
                        form["sourceSha256"].ToString(), form["collection"].ToString(), http.RequestAborted);
                    return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(JobPath(job.Database)) : Results.Json(job, Json.TerraBaseImportJob, statusCode: 201);
                }
                catch (Exception error) when (KnownFailure(http, error))
                {
                    return DatabaseEndpoints.WantsHtml(http) ? await ListPageAsync(http, context, imports, Message(error), Status(error)) : Failure(error);
                }
            });
            // One upload part of at most 4 MiB, passed to TerraBase; the only dashboard route above the 64 KiB body limit.
            endpoints.MapPut("/api/v1/dashboard/imports/{database}/source", async (HttpContext http, string database, DashboardSessions sessions, AccountService accounts, ImportService imports) =>
            {
                var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                if (!Csrf.Validate(http, new FormCollection(new() { ["_csrf"] = http.Request.Headers["X-CSRF-Token"].ToString() })))
                {
                    return Results.BadRequest("Invalid form token.");
                }

                if (http.Request.ContentLength is not { } bytes || bytes is < 1 or > ImportService.MaxPartBytes)
                {
                    return Results.Problem("Upload parts are 1 byte to 4 MiB.", statusCode: 413);
                }

                if (!long.TryParse(http.Request.Query["offset"], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                {
                    return Results.Problem("The upload offset is invalid.", statusCode: 400);
                }

                try
                {
                    // Viewers and nonmembers are refused here, before the body is read or buffered.
                    var token = await imports.AuthorizeUploadAsync(context, http.RequestAborted);
                    http.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = ImportService.MaxPartBytes;
                    var part = new byte[bytes];
                    await http.Request.Body.ReadExactlyAsync(part, http.RequestAborted);
                    return Results.Json(await imports.UploadAsync(context, database, offset, part, token, http.RequestAborted), Json.TerraBaseImportJob);
                }
                catch (Exception error) when (KnownFailure(http, error)) { return Failure(error); }
            });
            endpoints.MapGet("/imports/{database}", async Task<IResult> (HttpContext http, string database, DashboardSessions sessions, AccountService accounts, ImportService imports) =>
            {
                var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return AccountEndpoints.LoginRedirect(http);
                }

                TerraBaseImportJob job;
                try { job = await imports.JobAsync(context, database, http.RequestAborted); }
                catch (Exception error) when (KnownFailure(http, error)) { return await ListPageAsync(http, context, imports, Message(error), Status(error)); }
                var page = new ImportJobPage(ShellModel.For(http, context, "Import · " + job.Database), job);
                if (http.Request.Headers["Datastar-Request"] != "true")
                {
                    return ImportJobView.Create(page);
                }

                await DatastarEvents.PatchElementsAsync(http, ImportJobSection.Create(page));
                return Results.Empty;
            });
            endpoints.MapGet("/api/v1/dashboard/imports/{database}", async (HttpContext http, string database, DashboardSessions sessions, AccountService accounts, ImportService imports) =>
            {
                var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                try { return Results.Json(await imports.JobAsync(context, database, http.RequestAborted), Json.TerraBaseImportJob); }
                catch (Exception error) when (KnownFailure(http, error)) { return Failure(error); }
            });
            foreach (var operation in new[] { "options", "start", "cancel", "resume" })
            {
                var action = operation;
                endpoints.MapPost("/api/v1/dashboard/imports/{database}/" + action, async Task<IResult> (HttpContext http, string database, DashboardSessions sessions, AccountService accounts, ImportService imports) =>
                {
                    var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
                    if (context is null)
                    {
                        return Results.Unauthorized();
                    }

                    var form = await http.Request.ReadFormAsync(http.RequestAborted);
                    if (!Csrf.Validate(http, form))
                    {
                        return Results.BadRequest("Invalid form token.");
                    }

                    try
                    {
                        var job = action switch
                        {
                            "options" => await imports.UpdateAsync(context, database,
                                [.. form["exclude"].Select(value => value ?? "").Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)], http.RequestAborted),
                            "start" => await imports.StartAsync(context, database, form["previewId"].ToString(), form["confirm"] == "1", http.RequestAborted),
                            "cancel" => await imports.CancelAsync(context, database, http.RequestAborted),
                            _ => await imports.ResumeAsync(context, database, http.RequestAborted)
                        };
                        return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(JobPath(job.Database)) : Results.Json(job, Json.TerraBaseImportJob, statusCode: 202);
                    }
                    catch (Exception error) when (KnownFailure(http, error))
                    {
                        if (!DatabaseEndpoints.WantsHtml(http))
                        {
                            return Failure(error);
                        }

                        try
                        {
                            var job = await imports.JobAsync(context, database, http.RequestAborted);
                            var view = ImportJobView.Create(new(ShellModel.For(http, context, "Import · " + job.Database), job, Message(error)));
                            view.StatusCode = Status(error);
                            return view;
                        }
                        catch (Exception reload) when (KnownFailure(http, reload)) { return await ListPageAsync(http, context, imports, Message(error), Status(error)); }
                    }
                });
            }
            return endpoints;
        }

        public static string JobPath(string database)
        {
            return "/imports/" + Uri.EscapeDataString(database);
        }

        private const string Partial = "Some imports could not be read. Reload to try again.";

        private static async Task<IResult> ListPageAsync(HttpContext http, AccountContext context, ImportService imports, string error = "", int status = 200)
        {
            IReadOnlyList<TerraBaseImportJob> jobs = [];
            var unavailable = "";
            try
            {
                (jobs, var partial) = await imports.JobsAsync(context, http.RequestAborted); if (partial)
                {
                    unavailable = Partial;
                }
            }
            catch (Exception failure) when (KnownFailure(http, failure)) { unavailable = Message(failure); }
            var view = ImportsView.Create(new(ShellModel.For(http, context, "Import a database"), jobs, ImportService.NewDatabaseName(), unavailable, error));
            view.StatusCode = status;
            return view;
        }

        private static bool KnownFailure(HttpContext http, Exception error)
        {
            return (DatabaseEndpoints.KnownFailure(error) || error is HttpRequestException or JsonException or InvalidDataException or OperationCanceledException or BadHttpRequestException or EndOfStreamException) &&
            (error is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested);
        }

        private static int Status(Exception error)
        {
            return error switch { BadHttpRequestException bad => bad.StatusCode, EndOfStreamException => 400, _ => DatabaseEndpoints.Status(error) };
        }

        private static IResult Failure(Exception error)
        {
            return Results.Problem(Message(error), statusCode: Status(error));
        }

        // TerraBase HTTP error codes (contract v1 revision 3, section 9), as actions the user can take.
        private static string Message(Exception error)
        {
            return error switch
            {
                AccountAccessException => error.Message,
                BadHttpRequestException or EndOfStreamException => "The upload did not finish. Upload the same file again; it continues where it stopped.",
                TerraBaseException { ErrorCode: { } code } => code switch
                {
                    "destination_not_empty" => "The destination database already holds collections. Reload the page to import into a new database.",
                    "import_exists" => "This destination already has a different import. Reload the page to start a new one.",
                    "destination_replicated" => "The destination has replica regions. Start a new import; add replicas after it completes.",
                    "tenant_busy" or "tenant_memory" => "TerraBase is busy with this organization's requests. Try again in a moment.",
                    "offset_mismatch" => "TerraBase received a different part than expected. Upload the same file again; it continues where it stopped.",
                    "source_required" => "TerraBase no longer holds the export. Upload the same file again, then resume.",
                    "invalid_state" => "The import moved on. Reload to see its current state.",
                    "preview_changed" => "The preview changed after you reviewed it. Review the current preview and confirm again.",
                    "preview_blocked" => "Resolve the blocking issues before starting.",
                    "payload_too_large" => "Exports are limited to 1 GiB, in parts of at most 4 MiB.",
                    "capacity" => "TerraBase has no room or capacity for this import right now. Try again later.",
                    "forbidden_scope" => "Your access does not allow importing databases.",
                    "unknown_import" or "unknown_database" => "This import is unavailable.",
                    "wrong_region" => "This import lives in another region. Reload and try again.",
                    "invalid_request" => "TerraBase refused the import options. Reload and try again.",
                    _ => "The import service could not complete the request. Retrying is safe."
                },
                _ when DatabaseEndpoints.KnownFailure(error) => DatabaseEndpoints.Message(error),
                // The ts-auth exchange's refusals (IProjectAccessTokens): plain HttpRequestException carrying the status.
                HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "This project is not available to your account.",
                HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "Your project role does not allow this import action.",
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "Your sign-in expired. Sign in again to continue the import.",
                HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "Too many authorization requests. Try again in a moment.",
                _ => "The import service did not answer. Retrying is safe."
            };
        }
    }
}
