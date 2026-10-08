namespace TerraBase.Features.Documents;

/// <summary>Delivers bounded shape pages over a client subscription.</summary>
public interface IShapeSubscriptionService
{
    /// <summary>Streams until expiration, cancellation or a terminal feed error; refuses admission before starting the response.</summary>
    ValueTask<IResult> StreamAsync(HttpContext context, ShapeRequest request, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}
