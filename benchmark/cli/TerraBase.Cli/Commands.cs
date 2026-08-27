// <copyright file="Commands.cs" company="TerraScale">
// TerraBaseV2 (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli;

using System.Text.Json;
using System.Threading;

/// <summary>
/// The command bodies: one typed operation set over
/// <see cref="ITerraBaseSurface"/>, rendered by the two output
/// conventions. Human output uses the glossary vocabulary exactly —
/// the Change Event Backbone's ordered change events, the content-free
/// Change Notification, the Sync Session registry, the engine's
/// TopologyView — and never claims a Region, Member, or Failure Domain
/// the engine did not report.
/// </summary>
public static class Commands
{
    /// <summary>Dispatches one parsed command.</summary>
    /// <param name="command">The parsed command.</param>
    /// <param name="surface">The typed surface of this invocation's mode.</param>
    /// <param name="output">The output writer.</param>
    /// <param name="cancellation">The operator's interrupt.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> DispatchAsync(CliGrammar.Command command, ITerraBaseSurface surface, OutputWriter output, CancellationToken cancellation)
    {
        switch (command.Name)
        {
            case "status":
                return await StatusAsync(command, surface, output, cancellation).ConfigureAwait(false);
            case "document read":
                return await ReadAsync(command, surface, output, cancellation).ConfigureAwait(false);
            case "document write":
                return await WriteAsync(command, surface, output, cancellation).ConfigureAwait(false);
            case "document delete":
                return await DeleteAsync(command, surface, output, cancellation).ConfigureAwait(false);
            case "document browse":
                return await QueryAsync(command, $"from {command.Operands[0]}", surface, output, cancellation).ConfigureAwait(false);
            case "query":
                return await QueryAsync(command, command.Operands[0], surface, output, cancellation).ConfigureAwait(false);
            case "query cancel":
                return await CancelAsync(command, surface, output, cancellation).ConfigureAwait(false);
            case "changes":
                return await ChangesAsync(command, surface, output, cancellation).ConfigureAwait(false);
            case "sessions":
                return await SessionsAsync(command, surface, output, cancellation).ConfigureAwait(false);
            case "audit":
                return await AuditAsync(command, surface, output, cancellation).ConfigureAwait(false);
            case "metrics":
                return await MetricsAsync(command, surface, output, cancellation).ConfigureAwait(false);
            case "engine health":
                return await EngineViewAsync(command, surface, output, EngineView.Health, cancellation).ConfigureAwait(false);
            case "engine status":
                return await EngineViewAsync(command, surface, output, EngineView.Admin, cancellation).ConfigureAwait(false);
            case "engine topology":
                return await EngineViewAsync(command, surface, output, EngineView.Topology, cancellation).ConfigureAwait(false);
            case "engine shutdown":
                return await ShutdownAsync(command, surface, output, cancellation).ConfigureAwait(false);
            default:
                throw new SurfaceUsageException($"unknown command \"{command.Name}\"");
        }
    }

    private static async Task<int> StatusAsync(CliGrammar.Command command, ITerraBaseSurface surface, OutputWriter output, CancellationToken cancellation)
    {
        var status = await surface.ReadStatusAsync(cancellation).ConfigureAwait(false);

        output.Envelope(command.DisplayName, "result", writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("health");
            writer.WriteString("status", status.HealthStatus);
            WriteString(writer, "engineState", status.EngineState);
            WriteNumber(writer, "sessions", status.Sessions);
            WriteNumber(writer, "subscriptions", status.Subscriptions);
            WriteNumber(writer, "framesServed", status.FramesServed);
            writer.WriteNumber("generation", status.Generation);
            writer.WriteNumber("restarts", status.Restarts);
            WriteString(writer, "detail", status.Detail);
            writer.WriteEndObject();
            writer.WriteStartObject("admin");
            WriteString(writer, "registryVersion", status.RegistryVersion);
            WriteNumber(writer, "framesServed", status.FramesServed);
            WriteNumber(writer, "sessions", status.Sessions);
            WriteNumber(writer, "subscriptions", status.Subscriptions);
            WriteNumber(writer, "scopes", status.Scopes);
            writer.WriteEndObject();
            writer.WriteStartObject("topology");
            WriteString(writer, "binding", status.Binding);
            if (status.Subsystems is { } subsystems)
            {
                writer.WriteStartArray("subsystems");
                foreach (var subsystem in subsystems)
                {
                    writer.WriteStringValue(subsystem);
                }

                writer.WriteEndArray();
            }

            if (status.EngineTopologyAvailable is { } available)
            {
                writer.WriteBoolean("engineTopologyAvailable", available);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        });

        output.Human("Engine");
        output.Human($"  state           {status.EngineState ?? "unknown"}");
        output.Human($"  health          {status.HealthStatus}");
        output.Human($"  generation      {status.Generation}");
        output.Human($"  restarts        {status.Restarts}");
        output.Human($"  frames served   {status.FramesServed}");
        output.Human($"  sessions        {status.Sessions}");
        output.Human($"  subscriptions   {status.Subscriptions}");
        output.Human($"  scopes          {status.Scopes}");
        output.Human($"  registry        {status.RegistryVersion}");
        if (status.Detail is { } detail)
        {
            output.Human($"  detail          {detail}");
        }

        output.Human("TopologyView");
        output.Human($"  binding         {status.Binding ?? "unknown"}");
        output.Human($"  subsystems      {string.Join(", ", status.Subsystems ?? [])}");
        if (status.EngineTopologyAvailable == true)
        {
            output.Human("  engine topology available");
        }
        else
        {
            // Truthful, never invented: the engine publishes no
            // TopologyView at this binding, so no Region, Member, or
            // Failure Domain is summarized.
            output.Human("  engine topology not reported by this engine generation");
            output.Human("  Regions          not reported: the engine publishes no TopologyView at this binding");
            output.Human("  Members          not reported: the engine publishes no TopologyView at this binding");
            output.Human("  Failure Domains  not reported: the engine publishes no TopologyView at this binding");
        }

        return status.HealthStatus == "serving" ? Cli.ExitOk : Cli.ExitUnavailable;
    }

    private static async Task<int> ReadAsync(CliGrammar.Command command, ITerraBaseSurface surface, OutputWriter output, CancellationToken cancellation)
    {
        var (collection, document) = (command.Operands[0], command.Operands[1]);
        var answer = await surface.ReadDocumentAsync(collection, document, cancellation).ConfigureAwait(false);
        WriteDocument(output, command.DisplayName, answer);
        if (!answer.Found)
        {
            output.Human("not found");
            return Cli.ExitOk;
        }

        output.Human($"{answer.Id}  {answer.Collection}  version {answer.Version}");
        output.Human(BodyOf(answer));
        return Cli.ExitOk;
    }

    private static async Task<int> WriteAsync(CliGrammar.Command command, ITerraBaseSurface surface, OutputWriter output, CancellationToken cancellation)
    {
        var (collection, document) = (command.Operands[0], command.Operands[1]);
        using var parsed = ParseBody(command.Operands[2]);
        var key = command.Option("idempotency-key", $"cli-{Guid.NewGuid():N}");
        var answer = await surface.WriteDocumentAsync(collection, document, parsed.RootElement.Clone(), ExpectedOf(command), key, cancellation).ConfigureAwait(false);
        return WriteOf(output, command.DisplayName, "written", answer);
    }

    private static async Task<int> DeleteAsync(CliGrammar.Command command, ITerraBaseSurface surface, OutputWriter output, CancellationToken cancellation)
    {
        var (collection, document) = (command.Operands[0], command.Operands[1]);
        var key = command.Option("idempotency-key", $"cli-{Guid.NewGuid():N}");
        var answer = await surface.DeleteDocumentAsync(collection, document, ExpectedOf(command), key, cancellation).ConfigureAwait(false);
        return WriteOf(output, command.DisplayName, "deleted", answer);
    }

    private static async Task<int> QueryAsync(CliGrammar.Command command, string text, ITerraBaseSurface surface, OutputWriter output, CancellationToken cancellation)
    {
        var maxItems = ParseCount(command.Option("max-items", "256"), "--max-items");
        var answer = await surface.ExecuteQueryAsync(text, maxItems, cancellation).ConfigureAwait(false);

        output.Envelope(command.DisplayName, "result", writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("documents");
            foreach (var document in answer.Documents)
            {
                WriteDocumentBody(writer, document);
            }

            writer.WriteEndArray();
            writer.WriteNumber("count", answer.Count);
            writer.WriteString("terminal", answer.Terminal);
            writer.WriteEndObject();
        });

        foreach (var document in answer.Documents)
        {
            output.Human($"{document.Id}  {document.Collection}  version {document.Version}  {BodyOf(document)}");
        }

        output.Human($"count {answer.Count}  terminal {answer.Terminal}");
        return Cli.ExitOk;
    }

    private static async Task<int> CancelAsync(CliGrammar.Command command, ITerraBaseSurface surface, OutputWriter output, CancellationToken cancellation)
    {
        var queryId = ParseUlong(command.Operands[0], "QUERY-ID");
        var outcome = await surface.CancelQueryAsync(queryId, cancellation).ConfigureAwait(false);
        output.Envelope(command.DisplayName, "result", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("outcome", outcome);
            writer.WriteEndObject();
        });
        output.Human($"cancel  outcome={outcome}");
        return Cli.ExitOk;
    }

    private static async Task<int> ChangesAsync(CliGrammar.Command command, ITerraBaseSurface surface, OutputWriter output, CancellationToken cancellation)
    {
        var follow = command.Options.ContainsKey("follow");
        var resume = ParseUlong(command.Option("from", "1"), "--from");
        var idle = ParseBound(command.Option("idle", "1000"), "--idle");
        var maxEvents = command.Options.TryGetValue("max-events", out var bound) ? ParseBound(bound, "--max-events") : default(int?);
        var collections = command.Options.TryGetValue("collections", out var filter)
            ? filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

        var checkpoint = resume > 0 ? resume - 1 : 0;
        var events = 0;
        while (!cancellation.IsCancellationRequested)
        {
            IChangesTail tail;
            try
            {
                tail = await surface.OpenChangesAsync(resume, collections, cancellation).ConfigureAwait(false);
            }
            catch (SurfaceUnavailableException) when (follow)
            {
                // The tail's own bound: retry the connection after a
                // short delay until the operator interrupts.
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellation).ConfigureAwait(false);
                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var sawClosed = false;
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var read = tail.ReadAsync(linked.Token);
                    var completed = await Task.WhenAny(read, follow ? Never(linked.Token) : Task.Delay(idle, linked.Token)).ConfigureAwait(false);
                    if (completed != read)
                    {
                        // The bounded idle close of a non-following
                        // replay: the retained window is drained.
                        linked.Cancel();
                        Observe(read);
                        await tail.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                        Notice(output, command.DisplayName, "idle-close", checkpoint);
                        return Cli.ExitOk;
                    }

                    var frame = await read.ConfigureAwait(false);
                    if (frame is null)
                    {
                        if (follow && !sawClosed)
                        {
                            throw new ConnectionDroppedException("the stream ended without a close frame");
                        }

                        return Cli.ExitOk;
                    }

                    switch (frame.Kind)
                    {
                        case "opened":
                            Notice(output, command.DisplayName, "opened", frame.Sequence ?? checkpoint, $"subscription start sequence {frame.Sequence}");
                            break;
                        case "change-event":
                            events++;
                            checkpoint = Max(checkpoint, frame.Sequence ?? checkpoint);
                            output.Envelope(command.DisplayName, "event", writer =>
                            {
                                writer.WriteStartObject();
                                writer.WriteString("kind", "change-event");
                                WriteNumber(writer, "sequence", frame.Sequence);
                                WriteString(writer, "changeKind", frame.ChangeKind);
                                WriteString(writer, "collection", frame.Collection);
                                WriteString(writer, "document", frame.Document);
                                WriteString(writer, "operationId", frame.OperationId);
                                writer.WriteEndObject();
                            });
                            output.Human(
                                $"change event  sequence={frame.Sequence}  kind={frame.ChangeKind}  collection={frame.Collection}  document={frame.Document}  operationId={frame.OperationId}");
                            if (maxEvents is { } limit && events >= limit)
                            {
                                linked.Cancel();
                                Observe(read);
                                await tail.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                                return Cli.ExitOk;
                            }

                            break;
                        case "change-notification":
                            checkpoint = Max(checkpoint, frame.Sequence ?? checkpoint);
                            output.Envelope(command.DisplayName, "event", writer =>
                            {
                                writer.WriteStartObject();
                                writer.WriteString("kind", "change-notification");
                                WriteNumber(writer, "sequence", frame.Sequence);
                                WriteString(writer, "collection", frame.Collection);
                                WriteString(writer, "operationId", frame.OperationId);
                                writer.WriteEndObject();
                            });
                            output.Human(
                                $"Change Notification  collection={frame.Collection}  sequence={frame.Sequence}  operationId={frame.OperationId}  (content-free)");
                            break;
                        case "drop":
                            if (frame.ResumeFrom is > 0 and { } boundary)
                            {
                                checkpoint = Max(checkpoint, boundary - 1);
                            }

                            Notice(output, command.DisplayName, "drop", checkpoint, $"resume from sequence {frame.ResumeFrom}");
                            break;
                        case "closed":
                            sawClosed = true;
                            Notice(output, command.DisplayName, "closed", checkpoint, $"reason {frame.Reason}");
                            if (follow && frame.Reason == "engine-generation-lost")
                            {
                                throw new ConnectionDroppedException("the engine generation was lost");
                            }

                            await tail.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                            return Cli.ExitOk;
                        default:
                            break;
                    }
                }

                return Cli.ExitOk;
            }
            catch (ConnectionDroppedException)
            {
                // The checkpoint resume: the tail reopens strictly after
                // the last seen change event, so the missed range is
                // re-delivered exactly once, with no duplicate.
                Notice(output, command.DisplayName, "reconnect", checkpoint);
                output.Human($"reconnect  checkpoint={checkpoint}  (the connection dropped; resuming after the last seen change event)");
                resume = checkpoint + 1;
                linked.Cancel();
                await tail.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                await tail.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                linked.Cancel();
            }
        }

        return Cli.ExitOk;
    }

    private static async Task<int> SessionsAsync(CliGrammar.Command command, ITerraBaseSurface surface, OutputWriter output, CancellationToken cancellation)
    {
        var report = await surface.ReadSessionsAsync(cancellation).ConfigureAwait(false);
        output.Envelope(command.DisplayName, "result", writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("available", report.Available);
            if (report.Sessions is { } sessions)
            {
                writer.WriteStartArray("sessions");
                foreach (var session in sessions)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("sessionId", session.SessionId);
                    writer.WriteString("collection", session.Collection);
                    writer.WriteNumber("generation", session.Generation);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (report.EngineSessions is { } count)
            {
                writer.WriteNumber("engineSessions", count);
            }

            writer.WriteEndObject();
        });

        if (report.Available)
        {
            foreach (var session in report.Sessions ?? [])
            {
                output.Human($"Sync Session  {session.SessionId}  collection={session.Collection}  generation={session.Generation}");
            }

            if ((report.Sessions?.Count ?? 0) == 0)
            {
                output.Human("no live Sync Sessions on this host");
            }
        }
        else
        {
            output.Human($"the session registry is the API host's tracked state; the engine reports {report.EngineSessions} session(s)");
        }

        return Cli.ExitOk;
    }

    private static async Task<int> AuditAsync(CliGrammar.Command command, ITerraBaseSurface surface, OutputWriter output, CancellationToken cancellation)
    {
        var limit = ParseBound(command.Option("limit", "100"), "--limit");
        var records = await surface.ReadAuditAsync(limit, cancellation).ConfigureAwait(false);
        output.Envelope(command.DisplayName, "result", records);
        if (records.ValueKind == JsonValueKind.Object && records.TryGetProperty("records", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in list.EnumerateArray())
            {
                output.Human(
                    $"{StringOf(record, "kind")}  {StringOf(record, "operation")}  {StringOf(record, "outcome")}  {StringOf(record, "detail")}");
            }
        }

        return Cli.ExitOk;
    }

    private static async Task<int> MetricsAsync(CliGrammar.Command command, ITerraBaseSurface surface, OutputWriter output, CancellationToken cancellation)
    {
        var metrics = await surface.ReadMetricsAsync(cancellation).ConfigureAwait(false);
        output.Envelope(command.DisplayName, "result", metrics);
        if (metrics.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metrics.EnumerateObject())
            {
                output.Human($"{property.Name}  {property.Value}");
            }
        }

        return Cli.ExitOk;
    }

    private static async Task<int> EngineViewAsync(CliGrammar.Command command, ITerraBaseSurface surface, OutputWriter output, EngineView view, CancellationToken cancellation)
    {
        var status = await surface.ReadStatusAsync(cancellation).ConfigureAwait(false);
        var json = view switch
        {
            EngineView.Health => (json: ToJson(writer =>
            {
                writer.WriteString("status", status.HealthStatus);
                writer.WriteString("engineState", status.EngineState);
                WriteNumber(writer, "sessions", status.Sessions);
                WriteNumber(writer, "subscriptions", status.Subscriptions);
                WriteNumber(writer, "framesServed", status.FramesServed);
                writer.WriteNumber("generation", status.Generation);
                writer.WriteNumber("restarts", status.Restarts);
                WriteString(writer, "detail", status.Detail);
            }), human: $"state {status.EngineState}  health {status.HealthStatus}  generation {status.Generation}  restarts {status.Restarts}"),
            EngineView.Admin => (json: ToJson(writer =>
            {
                WriteString(writer, "registryVersion", status.RegistryVersion);
                WriteNumber(writer, "framesServed", status.FramesServed);
                WriteNumber(writer, "sessions", status.Sessions);
                WriteNumber(writer, "subscriptions", status.Subscriptions);
                WriteNumber(writer, "scopes", status.Scopes);
            }), human: $"registry {status.RegistryVersion}  frames served {status.FramesServed}  sessions {status.Sessions}  subscriptions {status.Subscriptions}  scopes {status.Scopes}"),
            _ => (json: ToJson(writer =>
            {
                WriteString(writer, "binding", status.Binding);
                if (status.Subsystems is { } subsystems)
                {
                    writer.WriteStartArray("subsystems");
                    foreach (var subsystem in subsystems)
                    {
                        writer.WriteStringValue(subsystem);
                    }

                    writer.WriteEndArray();
                }

                if (status.EngineTopologyAvailable is { } available)
                {
                    writer.WriteBoolean("engineTopologyAvailable", available);
                }
            }), human: $"binding {status.Binding}  subsystems {string.Join(", ", status.Subsystems ?? [])}  engine topology {(status.EngineTopologyAvailable == true ? "available" : "not reported")}"),
        };
        output.Envelope(command.DisplayName, "result", json.json);
        output.Human(json.human);
        return status.HealthStatus == "serving" ? Cli.ExitOk : Cli.ExitUnavailable;
    }

    private static async Task<int> ShutdownAsync(CliGrammar.Command command, ITerraBaseSurface surface, OutputWriter output, CancellationToken cancellation)
    {
        var reason = command.Option("reason", "operator");
        if (reason is not ("operator" or "supervisor"))
        {
            throw new SurfaceUsageException($"--reason is operator or supervisor, not \"{reason}\"");
        }

        var answer = await surface.ShutdownAsync(reason, cancellation).ConfigureAwait(false);
        output.Envelope(command.DisplayName, "result", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("state", answer.State);
            writer.WriteNumber("inFlight", answer.InFlight);
            writer.WriteEndObject();
        });
        output.Human($"shutdown  state={answer.State}  in-flight={answer.InFlight}");
        return Cli.ExitOk;
    }

    private static void WriteDocument(OutputWriter output, string command, DocumentReadOutcome answer)
    {
        output.Envelope(command, "result", writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("found", answer.Found);
            if (answer.Found)
            {
                writer.WritePropertyName("document");
                WriteDocumentBody(writer, answer);
            }

            writer.WriteEndObject();
        });
    }

    private static void WriteDocumentBody(Utf8JsonWriter writer, DocumentReadOutcome document)
    {
        writer.WriteStartObject();
        writer.WriteString("id", document.Id);
        writer.WriteString("collection", document.Collection);
        WriteNumber(writer, "version", document.Version);
        writer.WritePropertyName("body");
        if (document.Body is { } body)
        {
            body.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WriteEndObject();
    }

    private static int WriteOf(OutputWriter output, string command, string done, WriteOutcome answer)
    {
        output.Envelope(command, "result", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("outcome", answer.Outcome);
            if (answer.Sequence is { } sequence)
            {
                writer.WriteStartObject("receipt");
                writer.WriteNumber("sequence", sequence);
                WriteNumber(writer, "version", answer.Version);
                WriteString(writer, "operationId", answer.OperationId);
                writer.WriteEndObject();
            }

            WriteString(writer, "reason", answer.Reason);
            writer.WriteEndObject();
        });

        output.Human(answer.Outcome switch
        {
            "committed" => $"{done}  committed  sequence={answer.Sequence}  version={answer.Version}  operationId={answer.OperationId}",
            "conflict" => $"{done}  conflict (the expected version did not match)",
            _ => $"{done}  rejected  reason={answer.Reason}",
        });
        return answer.Outcome == "committed" ? Cli.ExitOk : Cli.ExitRefused;
    }

    private static string BodyOf(DocumentReadOutcome document) =>
        document.Body is { } body ? body.GetRawText() : "null";

    private static string? ExpectedOf(CliGrammar.Command command)
    {
        if (!command.Options.TryGetValue("expected", out var expected))
        {
            return null;
        }

        // Both modes refuse the same malformed conditional before any
        // call is sent, so the host mode cannot forward an unvalidated
        // expected-version header the engine would have to answer.
        return expected is "any" || ulong.TryParse(expected, out _)
            ? expected
            : throw new SurfaceUsageException($"--expected is any or an exact version, not \"{expected}\"");
    }

    /// <summary>
    /// Parses the body operand of <c>document write</c> fail-closed: a body that
    /// is not JSON is a usage refusal, never an unhandled parse failure.
    /// </summary>
    /// <param name="body">The body operand.</param>
    /// <returns>The parsed body document.</returns>
    private static JsonDocument ParseBody(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException failure)
        {
            throw new SurfaceUsageException($"the document write body is one JSON document, not \"{Snippet(body)}\" ({failure.Message})");
        }
    }

    private static string Snippet(string text) => text.Length <= 32 ? text : $"{text[..32]}…";

    private static ulong ParseUlong(string text, string name) =>
        ulong.TryParse(text, out var value) ? value : throw new SurfaceUsageException($"{name} is a sequence number, not \"{text}\"");

    /// <summary>Parses a non-negative count option (<c>--max-items</c>) fail-closed.</summary>
    /// <param name="text">The option value.</param>
    /// <param name="name">The option's name, for the bounded refusal.</param>
    /// <returns>The count.</returns>
    private static uint ParseCount(string text, string name) =>
        uint.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new SurfaceUsageException($"{name} is a whole number of items, not \"{text}\"");

    /// <summary>Parses a positive bound option (<c>--idle</c>, <c>--max-events</c>, <c>--limit</c>) fail-closed, before the tail or the read opens.</summary>
    /// <param name="text">The option value.</param>
    /// <param name="name">The option's name, for the bounded refusal.</param>
    /// <returns>The bound.</returns>
    private static int ParseBound(string text, string name) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new SurfaceUsageException($"{name} is a positive whole number, not \"{text}\"");

    private static void Notice(OutputWriter output, string command, string kind, ulong checkpoint, string? human = null)
    {
        output.Envelope(command, "notice", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteNumber("checkpoint", checkpoint);
            writer.WriteEndObject();
        });
        if (human is not null)
        {
            output.Human($"{kind}  {human}");
        }
    }

    private static JsonElement ToJson(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, ulong? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, uint? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, long value) => writer.WriteNumber(name, value);

    private static void WriteNumber(Utf8JsonWriter writer, string name, int value) => writer.WriteNumber(name, value);

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ulong Max(ulong left, ulong right) => left > right ? left : right;

    private static Task Never(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private static void Observe(Task<TailFrame?> read)
    {
        // The abandoned read's outcome is observed, never ignored: a
        // disposed stream's exception must not surface as noise.
        _ = read.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private enum EngineView
    {
        Health,
        Admin,
        Topology,
    }
}
