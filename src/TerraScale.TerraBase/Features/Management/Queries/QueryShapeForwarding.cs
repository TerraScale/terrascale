using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.Management.Queries;

/// <summary>One follower observation and its catalog owner; all empty owner ids select the dashboard.</summary>
/// <param name="OrganizationId">Owning organization.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="DatabaseId">Owning database.</param>
/// <param name="Stats">Bounded canonical shape statistics.</param>
public sealed record QueryShapeTransfer(
    [property: JsonRequired] string OrganizationId,
    [property: JsonRequired] string ProjectId,
    [property: JsonRequired] string DatabaseId,
    [property: JsonRequired] QueryShapeStats Stats);

/// <summary>A bounded batch, retained unchanged until acknowledged or its one-day retry window expires.</summary>
/// <param name="Items">The observations to add.</param>
/// <param name="FirstSentAt">Time the follower first attempted this batch.</param>
public sealed record QueryShapeForwardPayload(
    [property: JsonRequired] IReadOnlyList<QueryShapeTransfer> Items,
    [property: JsonRequired] DateTimeOffset FirstSentAt);

internal static class QueryShapeForwarding
{
    internal const string Format = "alpha";
    internal const int MaxItems = 128;
    internal const int MaxBytes = 1024 * 1024;
    internal const int ReservationBytes = 8 * 1024 * 1024;
    // Envelope plus bounded protobuf strings, epoch, tags and lengths.
    internal const int MaxMessageBytes = MaxBytes + 1024;
}

internal sealed class QueryShapeReceiver(TerraBaseOptions options, QueryShapeRecorder recorder, TimeProvider time)
    : CatalogQueryShapes.CatalogQueryShapesBase
{
    public override async Task<QueryShapesAck> Add(QueryShapesBatch request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (options.CatalogPrimary is not null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "this member is not a catalog primary"));
        }

        if (request.Format != QueryShapeForwarding.Format || request.Epoch.Length != 16 || request.Sequence is 0 or > long.MaxValue
            || request.Sender.Length is < 1 or > 128 || request.Envelope.Length is < ApplicationEnvelope.HeaderBytes or > QueryShapeForwarding.MaxBytes)
        {
            throw InvalidBatch();
        }

        QueryShapeForwardPayload payload;
        try
        {
            var decoded = ApplicationEnvelope.Decode(request.Envelope.Span, QueryShapeForwarding.MaxBytes);
            ValidateItemCount(decoded);
            payload = JsonSerializer.Deserialize(decoded, TerraBaseJsonContext.Default.QueryShapeForwardPayload) ?? throw InvalidBatch();
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            throw InvalidBatch();
        }

        var now = time.GetUtcNow();
        if (payload.Items is null || payload.Items.Count is < 1 or > QueryShapeForwarding.MaxItems
            || payload.FirstSentAt == default || payload.FirstSentAt < now.AddDays(-1) || payload.FirstSentAt > now.AddDays(1)
            || !payload.Items.All(Valid))
        {
            throw InvalidBatch();
        }

        // The recorder checks ownership and saves the sender watermark and statistics in the same write.
        return await recorder.AcceptAsync(request.Sender, request.Epoch.ToBase64(), (long)request.Sequence, payload, context.CancellationToken)
            .ConfigureAwait(false) == QueryShapeAccept.Capacity
            ? throw new RpcException(new Status(StatusCode.ResourceExhausted, "query shape receiver is at capacity"))
            : new QueryShapesAck();
    }

    private static bool Valid(QueryShapeTransfer item) => item is not null
        && ((item.OrganizationId == "" && item.ProjectId == "" && item.DatabaseId == "")
            || (ValidId(item.OrganizationId) && ValidId(item.ProjectId) && ValidId(item.DatabaseId)))
        && item.Stats is { } stats && QueryShapeRecorder.Valid(stats, $"{stats.Collection}:{stats.Id}");

    private static bool ValidId(string id) => id is { Length: > 0 and <= 128 }
        && EntityIds.IsValid(id);

    // Read tokens without a JSON DOM so a hostile list or unknown property cannot allocate a large
    // token tree. Check duplicate/case-insensitive items properties before materializing any DTO list.
    private static void ValidateItemCount(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw InvalidBatch();
        }

        var seenItems = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw InvalidBatch();
            }

            var items = reader.ValueTextEquals("items"u8) || string.Equals(reader.GetString(), "items", StringComparison.OrdinalIgnoreCase);
            if (!reader.Read())
            {
                throw InvalidBatch();
            }

            if (items)
            {
                if (seenItems || reader.TokenType != JsonTokenType.StartArray)
                {
                    throw InvalidBatch();
                }

                seenItems = true;
                var count = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (++count > QueryShapeForwarding.MaxItems)
                    {
                        throw InvalidBatch();
                    }

                    reader.Skip();
                }

                if (count == 0)
                {
                    throw InvalidBatch();
                }
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static RpcException InvalidBatch() => new(new Status(StatusCode.InvalidArgument, "invalid query shape batch"));
}

// Runs after routing/listener checks and before gRPC reads or materializes the request. No queue: at
// most four receives each hold the full allowance for protobuf, decoded JSON, DTOs and storage buffers.
internal sealed class QueryShapeAdmission(IMemoryBudget memory) : IMiddleware
{
    private int _receives;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.GetEndpoint()?.Metadata.GetMetadata<GrpcMethodMetadata>()?.ServiceType != typeof(QueryShapeReceiver))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (Interlocked.Increment(ref _receives) > 4)
        {
            Interlocked.Decrement(ref _receives);
            Refuse(context);
            return;
        }

        try
        {
            using var reservation = memory.TryReserve(MemoryLane.Replication, QueryShapeForwarding.ReservationBytes);
            if (reservation is null)
            {
                Refuse(context);
                return;
            }

            await next(context).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _receives);
        }
    }

    private static void Refuse(HttpContext context)
    {
        context.Response.ContentType = "application/grpc";
        context.Response.Headers["grpc-status"] = ((int)StatusCode.ResourceExhausted).ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["grpc-message"] = "query shape receiver is at capacity";
    }
}
