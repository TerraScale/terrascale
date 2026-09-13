// <copyright file="TypedApi.Queries.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

public sealed partial class TypedApi
{
    // Queries (forwarded typed operations)
    // ------------------------------------------------------------------

    internal async Task<IResult> QueryExecuteAsync(HttpContext context, QueryRequestDto? request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Text))
        {
            return Results.Json(
                new ErrorDto("input-invalid", "query text is required"),
                statusCode: StatusCodes.Status400BadRequest);
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

    // ------------------------------------------------------------------

}
