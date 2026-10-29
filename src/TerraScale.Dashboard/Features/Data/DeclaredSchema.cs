using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed record DeclaredSchemaPage(ShellModel Shell, DatabaseRecord Database, string Collection, string Inferred = "",
        string? Declared = null, string Draft = "{}", string Error = "", string Notice = "", bool Removing = false);

    public static class DeclaredSchemaEndpoints
    {
        private const int MaxSchemaBytes = 64 * 1024;

        public static IEndpointRouteBuilder MapDeclaredSchema(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapMethods("/databases/{databaseId}/schema", ["GET", "POST"], async Task<IResult> (HttpContext http, string databaseId,
                DashboardSessions sessions, AccountService accounts, DataService data, DatabaseService databases, IHttpClientFactory factory, IProjectAccessTokens tokens) =>
            {
                // HTML-safe C1 escapes can triple UTF-8 size; validate compact JSON separately.
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
                {
                    bodyLimit.MaxRequestBodySize = 3 * MaxSchemaBytes + 4096;
                }

                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return AccountRequest.LoginRedirect(http);
                }

                context = await databases.CurrentAsync(context, http.RequestAborted);
                DatabaseRecord database;
                try { database = DatabaseService.Database(context, databaseId); }
                catch (AccountAccessException error) { return DatabaseResults.PageFailure(http, context, error); }
                var page = new DeclaredSchemaPage(ShellModel.For(http, context, database.Name + " · Schema"), database, http.Request.Query["collection"].ToString());
                var status = 200;
                var submitted = HttpMethods.IsPost(http.Request.Method);
                try
                {
                    IFormCollection? form = null;
                    if (submitted)
                    {
                        form = await http.Request.ReadFormAsync(http.RequestAborted);
                        if (!Csrf.Validate(http, form))
                        {
                            return Results.BadRequest("Invalid form token.");
                        }

                        page = page with { Collection = form["collection"].ToString(), Draft = form["schema"].ToString(), Removing = form["operation"] == "remove" };
                        if (!context.CanManage)
                        {
                            throw new AccountAccessException("Organization management access is required.");
                        }
                    }
                    else if (page.Collection.Length == 0)
                    {
                        page = page with { Collection = (await data.CollectionsAsync(context, databaseId, http.RequestAborted)).FirstOrDefault()?.Name ?? "" };
                    }

                    if (page.Collection.Length > 0)
                    {
                        DataService.ValidCollection(page.Collection);
                        var scope = DatabaseService.Scope(context);
                        if (PlatformDatabases.IsPlatform(scope.OrganizationId))
                        {
                            throw PlatformDatabases.ReadOnly();
                        }

                        var home = await databases.HomeAsync(context, database, http.RequestAborted);
                        var access = await tokens.GetAsync(scope.Session!, scope.OrganizationId, scope.ProjectId, [submitted ? "documents:write" : "documents:read"], http.RequestAborted);
                        var sdk = new TerraBaseDocumentsClient(factory.CreateClient("platform"), new TerraBaseDocumentsClientOptions
                        { BaseAddress = home.Api, Region = home.Id, AccessTokenProvider = _ => ValueTask.FromResult(access) }).Database(scope.ProjectId, database.Name);
                        if (form is not null)
                        {
                            if (form["operation"] == "remove")
                            {
                                if (form["confirmName"] != page.Collection)
                                {
                                    throw new AccountAccessException("Type the collection name to confirm removal.", 422);
                                }

                                await sdk.DeleteDeclaredSchemaAsync(page.Collection, http.RequestAborted);
                            }
                            else if (form["operation"] == "apply")
                            {
                                JsonDocument schema;
                                try
                                {
                                    using var parsed = JsonDocument.Parse(page.Draft);
                                    var compact = JsonDraft.Compact(parsed.RootElement, htmlSafe: false);
                                    if (System.Text.Encoding.UTF8.GetByteCount(compact) > MaxSchemaBytes)
                                    {
                                        throw new AccountAccessException("Enter a JSON Schema of 64 KiB or less after compact UTF-8 encoding.", 422);
                                    }

                                    schema = JsonDocument.Parse(compact);
                                }
                                catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new AccountAccessException("Enter a valid JSON Schema object or boolean.", 422); }
                                using (schema)
                                {
                                    if (schema.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
                                    {
                                        throw new AccountAccessException("A JSON Schema must be an object or boolean.", 422);
                                    }

                                    await sdk.PutDeclaredSchemaAsync(page.Collection, schema.RootElement, http.RequestAborted);
                                }
                            }
                            else
                            {
                                throw new AccountAccessException("Choose apply or remove.", 422);
                            }

                            return HttpRedirects.SeeOther(DatabaseService.Path(databaseId, "schema", page.Collection) + "&saved=" + (form["operation"] == "remove" ? "removed" : "applied"));
                        }
                        var declared = await sdk.GetDeclaredSchemaAsync(page.Collection, http.RequestAborted);
                        var json = declared.Schema is { ValueKind: not JsonValueKind.Null } value ? JsonDraft.Format(value, MaxSchemaBytes) : "";
                        page = page with { Declared = json, Draft = json.Length > 0 ? json : "{}", Notice = http.Request.Query["saved"] == "applied" ? "Validation schema applied." : http.Request.Query["saved"] == "removed" ? "Validation schema removed." : "" };
                        page = page with { Inferred = JsonSerializer.Serialize(await data.SchemaAsync(context, databaseId, page.Collection, http.RequestAborted), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) };
                    }
                    else if (submitted)
                    {
                        throw new AccountAccessException("Choose a collection.", 422);
                    }
                }
                catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return Results.Problem("Schema submission is too large. Editor text allows at most 192 KiB plus 4 KiB of form overhead; compact JSON Schema must be 64 KiB or less.", statusCode: error.StatusCode);
                }
                catch (Exception error) when (DatabaseResults.KnownFailure(error) || error is HttpRequestException or JsonException or IOException)
                {
                    status = DatabaseResults.Status(error);
                    page = page with { Error = status == 413 ? "The encoded schema exceeds the data API's 64 KiB request limit. Reduce its size and try again. Your draft has been kept." : DatabaseResults.Message(error) };
                }
                var view = DeclaredSchemaView.Create(page);
                view.StatusCode = status;
                return view;
            });
            return endpoints;
        }
    }
}
