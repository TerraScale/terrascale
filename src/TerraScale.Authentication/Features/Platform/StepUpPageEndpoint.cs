using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Fresh verification for an existing purpose-bound request. GET never sends mail.</summary>
public sealed class StepUpPageEndpoint : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(StepUpHosted.Route);
        Name("ui_step_up");
        Summary("Verify a sensitive operation");
        Description("Requires the request account's hosted session, then fresh password or request-bound email proof and active MFA. GET only renders; no email or approval occurs.");
        Tags("Security");
        Produces<string>(200, "text/html");
        Produces<TerraScale.Authentication.Contracts.ErrorBody>(403);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => await StepUpHosted.HandleAsync(this.HttpContext, false, cancellationToken);
}
