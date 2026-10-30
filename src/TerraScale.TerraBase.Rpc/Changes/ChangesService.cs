using System.Text;
using System.Text.Json;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Contracts.Changes;

namespace TerraScale.TerraBase.Rpc.Changes
{
    /// <summary>Streams the existing SSE changes and snapshot NDJSON export through the authorized REST pipeline.</summary>
    public sealed class ChangesService(RestPipeline pipeline, IServiceScopeFactory scopes) : ServiceBase<IChangesService>, IChangesService
    {
        /// <inheritdoc />
        public async Task<ServerStreamingResult<TerraBaseChangesEvent>> StreamChangesAsync(
            string projectId, string database, string? cursor, string? lastEventId, IReadOnlyList<string>? collections)
        {
            var stream = GetServerStreamingContext<TerraBaseChangesEvent>();
            var parser = new ChangeEvents(stream.WriteAsync);
            var path = RestPaths.Database(projectId, database) + "/changes" + RestPaths.Query(
                ("cursor", lastEventId is null ? cursor : null), ("collections", collections is null ? null : string.Join(',', collections)));
            await new RestLines(pipeline, scopes).SendAsync(Context, path, "text/event-stream", parser.LineAsync,
                headers => { if (lastEventId is not null) { headers["Last-Event-ID"] = lastEventId; } });
            return stream.Result();
        }

        /// <inheritdoc />
        public async Task<ServerStreamingResult<TerraBaseExportLine>> ExportAsync(string projectId, string database)
        {
            var stream = GetServerStreamingContext<TerraBaseExportLine>();
            await new RestLines(pipeline, scopes).SendAsync(Context, RestPaths.Database(projectId, database) + "/export", "application/x-ndjson",
                async (line, _) =>
                {
                    var item = JsonSerializer.Deserialize(line.Span, ChangesJson.Default.TerraBaseExportLine)
                        ?? throw new JsonException("The export contains an invalid document.");
                    await stream.WriteAsync(item);
                });
            return stream.Result();
        }

        private sealed class ChangeEvents(Func<TerraBaseChangesEvent, Task> send)
        {
            private string? checkpoint;
            private string? id;
            private string type = "message";
            private readonly StringBuilder data = new();

            internal async Task LineAsync(ReadOnlyMemory<byte> bytes, string? region)
            {
                var line = Encoding.UTF8.GetString(bytes.Span).TrimEnd('\r');
                if (line.Length > 0)
                {
                    if (line.StartsWith(':'))
                    {
                        return;
                    }

                    var colon = line.IndexOf(':', StringComparison.Ordinal);
                    var name = colon < 0 ? line : line[..colon];
                    var value = colon < 0 ? string.Empty : line[(colon + 1)..];
                    if (value.StartsWith(' '))
                    {
                        value = value[1..];
                    }

                    switch (name)
                    {
                        case "id": id = value; break;
                        case "event": type = value; break;
                        case "data":
                            if (data.Length + value.Length > TerraScale.TerraBase.Contracts.TerraBaseRpc.MaxMessageBytes)
                            {
                                throw new InvalidDataException("The changes event exceeds the RPC message limit.");
                            }

                            data.Append(value).Append('\n');
                            break;
                    }

                    return;
                }

                if (data.Length > 0)
                {
                    var payload = Encoding.UTF8.GetBytes(data.ToString());
                    if (type == "error")
                    {
                        using var error = JsonDocument.Parse(payload);
                        var code = error.RootElement.GetProperty("error").GetProperty("code").GetString();
                        var status = code switch
                        {
                            "cursor_expired" => 410,
                            "unknown_database" or "not_found" => 404,
                            "tenant_memory" => 429,
                            _ => 503,
                        };
                        throw RestApi.Fault(new RestAnswer(status, new Microsoft.AspNetCore.Http.HeaderDictionary(), payload));
                    }

                    if (type is "change" or "cursor")
                    {
                        checkpoint = id ?? checkpoint;
                        TerraBaseChange? change = null;
                        if (type == "change")
                        {
                            change = JsonSerializer.Deserialize(payload, ChangesJson.Default.TerraBaseChange) ?? throw new JsonException("Invalid change event.");
                        }
                        else
                        {
                            using var parsed = JsonDocument.Parse(payload);
                            if (id is null || parsed.RootElement.GetProperty("cursor").GetString() != id)
                            {
                                throw new JsonException("Invalid cursor event.");
                            }
                        }

                        await send(new(checkpoint, change, region));
                    }
                }

                id = null;
                type = "message";
                data.Clear();
            }
        }
    }
}
