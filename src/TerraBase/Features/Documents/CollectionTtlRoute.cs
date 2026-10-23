using TerraBase.Errors;

namespace TerraBase.Features.Documents;

internal static class CollectionTtlRoute
{
    public const string Route = "/v1/projects/{project}/databases/{database}/collections/{collection}/ttl";

    public static IResult Answer(CollectionTtlResult result) => result switch
    {
        CollectionTtlBody body => TypedResults.Ok(body),
        NotFound error => ErrorResponse.From(error),
        PayloadTooLarge error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
    };
}
