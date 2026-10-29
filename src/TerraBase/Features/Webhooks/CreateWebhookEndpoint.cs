using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Webhooks;

/// <summary>Registers a collection's public HTTPS change receiver.</summary>
internal sealed class CreateWebhookEndpoint(IManagementService management, IWebhooks webhooks) : Endpoint<CreateWebhookRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(WebhookRoute.Collection);
        Name("createCollectionWebhook");
        Summary("Registers a collection change webhook");
        Description(WebhookRoute.Description + " Body {url}; returns a generated signing secret only once. Starts at the current local feed head. Maximum 32 registrations per collection.");
        Tags("Webhooks");
        Metadata(new RequestBodyMetadata(typeof(CreateWebhookRequest), "application/json"));
        Produces<CreatedWebhookBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CreateWebhookRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WebhookRoute.RunAsync(HttpContext, management,
            (tenant, collection) => webhooks.CreateAsync(tenant, HttpContext.Request.RouteValues["databaseName"]!.ToString()!, collection, request.Url, cancellationToken),
            cancellationToken);
    }
}
