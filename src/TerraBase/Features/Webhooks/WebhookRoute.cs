using System.Globalization;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Webhooks;

internal static class WebhookRoute
{
    public const string Collection = ProjectApiRoute.Database + "/collections/{collection}/webhooks";
    public const string Item = Collection + "/{webhook}";
    public const string Description = "Project-token collection webhooks. Read requires documents:read; create/delete require both documents:read and documents:write. "
        + "Secrets are returned only by creation. Delivery uses signed HTTPS requests, durable retries and visible feed_gap on missing retained history. "
        + ProjectApiRoute.IdentityDescription;

    public static async ValueTask<IResult> RunAsync(HttpContext context, IManagementService management,
        Func<TenantContext, string, ValueTask<WebhookResult>> operation, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!DatabaseName.TryParse(context.Request.RouteValues["collection"] as string, out var collection))
        {
            return ErrorResponse.From(new Validation("collection must be a lowercase name of at most 63 characters"));
        }

        if (context.Request.RouteValues["webhook"] is string id && !EntityIds.IsValid(id))
        {
            return ErrorResponse.From(new Validation("webhook must be a canonical uppercase ULID"));
        }

        return await ManagementRoute.DatabaseAsync(context, management, cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext database => Result(context, await operation(database, collection.Value).ConfigureAwait(false)),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }

    public static Valid<PageQuery> Page(HttpContext context)
    {
        var query = context.Request.Query;
        var after = query.TryGetValue("after", out var rawAfter) ? rawAfter.ToString() : null;
        var limit = 50;
        if (query.Keys.Any(key => key is not ("after" or "limit")) || after is not null && !EntityIds.IsValid(after)
            || query.TryGetValue("limit", out var rawLimit) && (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100))
        {
            return new Validation("after must be a canonical uppercase ULID; limit must be an integer from 1 to 100");
        }

        return new PageQuery(after, limit);
    }

    private static IResult Result(HttpContext context, WebhookResult result) => result switch
    {
        CreatedWebhookBody created => TypedResults.Created(context.Request.Path.Value!.TrimEnd('/') + "/" + created.Id, created),
        WebhookBody[] hooks => TypedResults.Ok(hooks),
        WebhookDeliveryBody[] deliveries => TypedResults.Ok(deliveries),
        Done => TypedResults.NoContent(),
        Validation error => ErrorResponse.From(error),
        NotFound error => ErrorResponse.From(error),
        Conflict error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
    };
}
