using TerraBase.Features.Memory;

namespace TerraBase.Features.Management.Queries;

/// <summary>Statistics whose admitted memory remains reserved until the response finishes.</summary>
public sealed class QueryShapeRead : IResult, IDisposable
{
    private readonly IMemoryReservation _reservation;

    internal QueryShapeRead(QueryShapeListBody body, IMemoryReservation reservation)
    {
        Body = body;
        _reservation = reservation;
    }

    /// <summary>The admitted response body.</summary>
    public QueryShapeListBody Body { get; }

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        try
        {
            await TypedResults.Ok(Body).ExecuteAsync(httpContext).ConfigureAwait(false);
        }
        finally
        {
            Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _reservation.Dispose();
}
