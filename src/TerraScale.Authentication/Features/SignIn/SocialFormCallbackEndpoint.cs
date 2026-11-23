using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Contracts;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class SocialFormCallbackEndpoint(SocialFlow flow) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/social/{provider}/callback");
        Name("ui_social_callback_form");
        Summary("Finish a social sign-in (form post)");
        Description(SocialFlow.CallbackDescription);
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var form = this.HttpContext.Request.HasFormContentType ? await this.HttpContext.Request.ReadFormAsync(cancellationToken) : null;
        return await flow.CompleteAsync(this.HttpContext, form?["code"], form?["state"], cancellationToken);
    }
}
