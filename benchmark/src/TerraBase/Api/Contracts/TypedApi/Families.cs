// <copyright file="TypedApi.Families.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using TerraBase.Protocol;

public sealed partial class TypedApi
{
    // Families (forwarded typed operations)
    // ------------------------------------------------------------------

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
