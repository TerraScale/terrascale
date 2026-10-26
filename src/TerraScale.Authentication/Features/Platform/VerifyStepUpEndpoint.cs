using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Same-origin hosted verification, charged against the existing credential budgets.</summary>
public sealed class VerifyStepUpEndpoint : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(StepUpHosted.Route);
        Name("ui_verify_step_up");
        Summary("Prove fresh credentials for a sensitive operation");
        Description("Same-origin form POST. Password: password; passwordless: action=send requests one code per request, then code redeems it. Active MFA follows as a second code form (TOTP or recovery). No proof secret appears in HTML or URLs. Success approves the durable pending proof and shows a safe continuation to its validated callback. Refused verification: 401; stale request, wrong account or CSRF: 403; exhausted budgets: 429.");
        Tags("Security");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(200, "text/html");
        Produces<string>(401, "text/html");
        Produces<OAuthErrorBody>(429);
        foreach (var status in new[] { 403, 503 })
        {
            Produces<ErrorBody>(status);
        }

        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => await StepUpHosted.HandleAsync(this.HttpContext, true, cancellationToken);
}
