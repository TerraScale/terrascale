using TerraBase.Errors;

namespace TerraBase.Features.Documents;

internal static class CollectionRulesRoute
{
    public const string Route = DocumentRoute.Collections + "/{collection}/rules";

    public static IResult Answer(CollectionRulesResult result) => result switch
    {
        CollectionRulesBody body => TypedResults.Json(body, CollectionRulesJsonContext.Default.CollectionRulesBody),
        NotFound error => ErrorResponse.From(error),
        Validation error => ErrorResponse.From(error),
        PayloadTooLarge error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
    };
}
