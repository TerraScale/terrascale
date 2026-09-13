// <copyright file="TypedApi.Streams.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TerraBase.Protocol;

public sealed partial class TypedApi
{
    // Subscription endpoints (SSE and WebSocket over the backbone)
    // ------------------------------------------------------------------

    internal async Task StreamSseAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            await refused.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var (collections, invalid) = this.Collections(context);
        if (invalid is not null)
        {
            await invalid.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var caller = ResolveCaller(context);
        var from = FromSequence(context, context.Request.Headers["Last-Event-ID"].ToString());
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await using var writer = new StreamWriter(context.Response.Body, new UTF8Encoding(false)) { AutoFlush = false };
        await this.PumpAsync(
            context,
            caller,
            collections,
            from,
            async (name, id, payload) =>
            {
                await writer.WriteLineAsync($"id: {id}").ConfigureAwait(false);
                await writer.WriteLineAsync($"event: {name}").ConfigureAwait(false);
                await writer.WriteLineAsync($"data: {JsonSerializer.Serialize(payload, Json.Options)}").ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task StreamWebSocketAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            await refused.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var (collections, invalid) = this.Collections(context);
        if (invalid is not null)
        {
            await invalid.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var caller = ResolveCaller(context);
        var from = FromSequence(context, context.Request.Query["from"].ToString());
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var send = new SemaphoreSlim(1, 1);
        try
        {
            await this.PumpAsync(
                context,
                caller,
                collections,
                from,
                async (name, id, payload) =>
                {
                    await send.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        payload["kind"] = name;
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json.Options);
                        await socket.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        send.Release();
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.Net.WebSockets.WebSocketException)
        {
            // The consumer went away; the stream closes cleanly below.
        }
    }

    private async Task PumpAsync(
        HttpContext context,
        Caller caller,
        string[]? collections,
        ulong? from,
        Func<string, ulong, Dictionary<string, object?>, Task> writer,
        CancellationToken cancellationToken)
    {
        BackboneStream stream;
        try
        {
            stream = await this.hub.OpenAsync(caller.Tenant, caller.Principal, collections, from, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            context.Response.StatusCode = failure switch
            {
                EngineUnavailableException => StatusCodes.Status503ServiceUnavailable,
                TbepException => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status500InternalServerError,
            };
            if (context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.Ordinal) != true)
            {
                await context.Response.WriteAsJsonAsync(new ErrorDto("stream-refused", "the stream could not be opened"), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        try
        {
            await writer("opened", stream.StartSequence, new Dictionary<string, object?>
            {
                ["subscriptionId"] = stream.SubscriptionId,
                ["startSequence"] = stream.StartSequence,
                ["collections"] = stream.Collections,
            }).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested
                && await stream.ReadAsync(cancellationToken).ConfigureAwait(false) is { } frame)
            {
                switch (frame)
                {
                    case StreamEvent.Batch batch:
                        await writer(
                            "batch",
                            batch.Body.NextSequence > 0 ? batch.Body.NextSequence - 1 : 0,
                            new Dictionary<string, object?>
                            {
                                ["subscriptionId"] = batch.Body.SubscriptionId,
                                ["fromSequence"] = batch.Body.FromSequence,
                                ["events"] = batch.Body.Events.Select(e => new Dictionary<string, object?>
                                {
                                    ["sequence"] = e.Sequence,
                                    ["operationId"] = e.OperationId,
                                    ["kind"] = e.Kind.Variant.ToSnakeCase(),
                                    ["collection"] = e.Collection,
                                    ["document"] = e.Document,
                                }).ToArray(),
                                ["nextSequence"] = batch.Body.NextSequence,
                            }).ConfigureAwait(false);
                        break;
                    case StreamEvent.Trigger trigger:
                        await writer(
                            "trigger",
                            trigger.Body.Sequence,
                            new Dictionary<string, object?>
                            {
                                ["subscriptionId"] = trigger.Body.SubscriptionId,
                                ["collection"] = trigger.Body.Collection,
                                ["sequence"] = trigger.Body.Sequence,
                                ["operationId"] = trigger.Body.OperationId,
                            }).ConfigureAwait(false);
                        break;
                    case StreamEvent.Closed closed:
                        await writer(
                            "closed",
                            stream.LastSequence,
                            new Dictionary<string, object?>
                            {
                                ["subscriptionId"] = closed.Body.SubscriptionId,
                                ["reason"] = closed.Body.Reason.Variant.ToSnakeCase(),
                                ["dropped"] = stream.Dropped,
                            }).ConfigureAwait(false);
                        break;
                    case StreamEvent.Dropped drop:
                        // The event id is the consumer's safe checkpoint:
                        // the last sequence still serveable without a
                        // hole (one before the dropped region's boundary,
                        // which the payload carries) — a reconnect with
                        // that id re-delivers the missed range.
                        await writer(
                            "drop",
                            drop.ResumeFrom > 0 ? drop.ResumeFrom - 1 : 0,
                            new Dictionary<string, object?>
                            {
                                ["subscriptionId"] = stream.SubscriptionId,
                                ["resumeFrom"] = drop.ResumeFrom,
                                ["dropped"] = stream.Dropped,
                            }).ConfigureAwait(false);
                        break;
                    case StreamEvent.GenerationLost:
                        await writer(
                            "closed",
                            stream.LastSequence,
                            new Dictionary<string, object?>
                            {
                                ["subscriptionId"] = stream.SubscriptionId,
                                ["reason"] = "engine-generation-lost",
                                ["dropped"] = stream.Dropped,
                                ["resumeFrom"] = stream.LastSequence,
                            }).ConfigureAwait(false);
                        break;
                }
            }
        }
        finally
        {
            await this.hub.CloseAsync(stream, reason: "consumer-disconnected").ConfigureAwait(false);
        }
    }

    internal async Task<IResult> StreamAcknowledgeAsync(HttpContext context, ulong id, StreamAckDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "backbone.acknowledge",
                    channel => channel.BackboneAcknowledgeAsync(id, request.ThroughSequence, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new StreamAckResponseDto(answer.SubscriptionId, answer.AcknowledgedThrough));
        }
        catch (TbepException failure) when (failure.Code?.Variant == "SubscriptionRefused")
        {
            return this.TenantOrStale(failure, caller, id, "subscription");
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> StreamCloseAsync(HttpContext context, ulong id, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        if (this.hub.TryGet(id, out var stream)
            && stream is not null
            && stream.Tenant == caller.Tenant
            && stream.Principal == caller.Principal)
        {
            try
            {
                // The hub owns the live stream and its pump. Closing through
                // it removes the host registry entry and lets the engine's
                // close notification drain before the queue completes.
                await this.hub.CloseAsync(stream, reason: "consumer-requested").ConfigureAwait(false);
                return Results.Json(new StreamAckResponseDto(id, 0));
            }
            catch (Exception failure)
            {
                return this.Failure(failure, caller);
            }
        }

        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "backbone.unsubscribe",
                    channel => channel.BackboneUnsubscribeAsync(id, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            this.audit.Record(
                AuditKind.SubscriptionClosed,
                caller.Tenant,
                caller.Principal,
                operation: "backbone.unsubscribe",
                outcome: AuditOutcome.Accepted,
                detail: $"subscription {id}");
            return Results.Json(new StreamAckResponseDto(answer.SubscriptionId, 0));
        }
        catch (TbepException failure) when (failure.Code?.Variant == "SubscriptionRefused")
        {
            return this.TenantOrStale(failure, caller, id, "subscription");
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    // ------------------------------------------------------------------

}
