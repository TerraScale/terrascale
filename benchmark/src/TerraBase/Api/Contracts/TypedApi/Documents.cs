// <copyright file="TypedApi.Documents.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using TerraBase.Protocol;

public sealed partial class TypedApi
{
    // Documents (forwarded typed operations)
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

    // ------------------------------------------------------------------

}
