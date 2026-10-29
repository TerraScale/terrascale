using TerraScale.Dashboard.Platform.Observability;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;

namespace TerraScale.Dashboard.Features.Data
{
    public static class DocumentChangesEndpoints
    {
        public static IEndpointRouteBuilder MapDocumentChanges(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/v1/dashboard/databases/{databaseId}/changes", ReadAsync)
                .WithGroupName("integration").WithName("ReadDocumentChanges")
                .WithSummary("Stream document changes for one collection")
                .WithDescription("Requires the current session and project membership. Last-Event-ID overrides cursor; retain the same collection on resume. Events are change, cursor, reconnect and resync_required. On resync_required discard the checkpoint, connect with cursor=now before rereading documents, and report unavailable retained history. This is not a live SQL subscription. Reconnecting exchanges fresh project authority; mutations are never replayed.")
                .Produces<DocumentChangesUpdate>(200, "text/event-stream")
                .ProducesProblem(400).Produces(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503);
            return endpoints;
        }

        private static async Task ReadAsync(HttpContext http, string databaseId, [FromQuery] string collection,
            [FromQuery] string? cursor, [FromHeader(Name = "Last-Event-ID")] string? lastEventId,
            DashboardSessions sessions, AccountService accounts, DocumentChangesService changes)
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) { await Results.Unauthorized().ExecuteAsync(http); return; }
            try
            {
                await foreach (var update in changes.ReadAsync(context, databaseId, collection, cursor, lastEventId, http.RequestAborted))
                {
                    if (update.Cursor is { } checkpoint && checkpoint.Any(character => character is '\r' or '\n' or '\0'))
                    {
                        throw new HttpRequestException("The changes feed returned an invalid checkpoint.");
                    }

                    if (!http.Response.HasStarted)
                    {
                        http.Response.ContentType = "text/event-stream";
                        http.Response.Headers.CacheControl = "no-store";
                        http.Response.Headers["X-Accel-Buffering"] = "no";
                    }
                    var id = update.Cursor is { } value ? "id: " + value + "\n" : "";
                    var json = JsonSerializer.Serialize(update, DocumentChangesJsonContext.Default.DocumentChangesUpdate);
                    await http.Response.WriteAsync($"event: {update.State}\n{id}data: {json}\n\n", http.RequestAborted);
                    await http.Response.Body.FlushAsync(http.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { ServiceSloTelemetry.MarkBad(http); }
            catch (Exception error) when (DatabaseEndpoints.KnownFailure(error) || error is HttpRequestException)
            {
                // Before the first event keep deterministic failures visible as HTTP errors.
                // After headers are sent terminate the stream; the reader retains its last checkpoint.
                if (http.Response.HasStarted) { ServiceSloTelemetry.MarkBad(http); http.Abort(); }
                else
                {
                    await DatabaseEndpoints.Failure(error).ExecuteAsync(http);
                }
            }
        }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(DocumentChangesUpdate))]
    internal sealed partial class DocumentChangesJsonContext : JsonSerializerContext;
}
