using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Contracts;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary><c>POST /ui/{slug}/social/{provider}/callback</c>: Apple's form post back.</summary>
public sealed class SocialFormCallbackEndpoint(SocialFlow flow) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/social/{provider}/callback");
        Name("ui_social_callback_form");
        Summary("Finish a social sign-in (form post)");
        Description(SocialFlow.CallbackDescription);
        Tags("Sign-in");
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        // Cross-site by design (the provider's page posts it): state and the state cookie, not Origin, bind it to this browser.
        var form = this.HttpContext.Request.HasFormContentType ? await this.HttpContext.Request.ReadFormAsync(cancellationToken) : null;
        return await flow.CompleteAsync(this.HttpContext, form?["code"], form?["state"], cancellationToken);
    }
}
