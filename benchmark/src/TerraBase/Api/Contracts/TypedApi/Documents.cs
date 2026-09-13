// <copyright file="TypedApi.Documents.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TerraBase.Protocol;

public sealed partial class TypedApi
{
    // Documents, queries, families (forwarded typed operations)
    // ------------------------------------------------------------------

    internal async Task<IResult> DocumentReadAsync(HttpContext context, string collection, string document, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (this.Scope(context, document) is { } invalidDocument)
        {
            return invalidDocument;
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "document.read", channel => channel.DocumentReadAsync(collection, document, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return answer.Found
                ? Results.Json(new DocumentReadDto(true, DocumentBodyDto.Of(answer.Document!)))
                : Results.Json(new DocumentReadDto(false, null));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> DocumentWriteAsync(HttpContext context, string collection, string document, JsonValue? body, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (this.Scope(context, document) is { } invalidDocument)
        {
            return invalidDocument;
        }

        if (Idempotency(context) is { } badKey)
        {
            return badKey;
        }

        var caller = ResolveCaller(context);
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var put = new DocumentPut { Id = document, Collection = collection, Body = body?.ToValue() ?? TbepValue.Null };
            // The operation id of a mutation is its idempotency key —
            // the retry identity — never the per-request trace id, so a
            // retried write names the same journal operation.
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "document.write",
                    channel => channel.DocumentWriteAsync(put, Expected(context), idempotencyKey, idempotencyKey, cancellationToken: cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(WriteOutcomeDto.Of(answer.Outcome));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> DocumentDeleteAsync(HttpContext context, string collection, string document, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (this.Scope(context, document) is { } invalidDocument)
        {
            return invalidDocument;
        }

        if (Idempotency(context) is { } badKey)
        {
            return badKey;
        }

        var caller = ResolveCaller(context);
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "document.delete",
                    channel => channel.DocumentDeleteAsync(collection, document, Expected(context), idempotencyKey, idempotencyKey, cancellationToken: cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(WriteOutcomeDto.Of(answer.Outcome));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> QueryExecuteAsync(HttpContext context, QueryRequestDto request, CancellationToken cancellationToken)
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
                    "query.execute",
                    channel => channel.QueryExecuteAsync(NextQueryId(), request.Text, request.MaxItems ?? 256, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new QueryResponseDto(
                answer.Documents.Select(DocumentBodyDto.Of).ToArray(),
                answer.Count,
                answer.Terminal.Variant.ToLowerInvariant()));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> QueryCancelAsync(HttpContext context, QueryCancelDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "query.cancel", channel => channel.QueryCancelAsync(request.QueryId, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new QueryCancelResponseDto(answer.Outcome.Variant.ToSnakeCase()));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> FamilyReadAsync(HttpContext context, string family, string name, FamilyRequestDto request, CancellationToken cancellationToken)
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
                    "family.read",
                    channel => channel.FamilyReadAsync(
                        new FamilyReadRequest { Family = family, Name = name, RequestCodec = request.RequestCodec, Request = request.RequestBytes },
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new FamilyResponseDto(answer.ResponseCodec, answer.Response));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> FamilyWriteAsync(HttpContext context, string family, string name, FamilyRequestDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (Idempotency(context) is { } badKey)
        {
            return badKey;
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "family.write",
                    channel => channel.FamilyWriteAsync(
                        new FamilyWriteRequest
                        {
                            Family = family,
                            Name = name,
                            RequestCodec = request.RequestCodec,
                            Request = request.RequestBytes,
                            OperationId = context.Request.Headers["Idempotency-Key"].ToString(),
                        },
                        context.Request.Headers["Idempotency-Key"].ToString(),
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new FamilyResponseDto(answer.ResponseCodec, answer.Response));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    // ------------------------------------------------------------------

}
