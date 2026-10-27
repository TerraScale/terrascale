using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Starts a dedicated hosted reauthentication without approving any operation.</summary>
public sealed class RequestStepUpEndpoint(StepUpProofs proofs, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/terrascale/step-up");
        this.Document("request_step_up", "Start purpose-bound reauthentication", "Creates a pending five-minute proof bound to the account, authentication epoch/state, purpose and audience. Supported: delete_account (ts-auth or ts-dashboard), create_pat (ts-dashboard), create_api_key, rotate_api_key and create_oauth_client (ts-auth). Keep proof on the backend; send the browser only to step_up_url. return_uri must be /auth/step-up/callback on a registered dashboard redirect origin, without query or fragment. Fresh primary credentials and active MFA are mandatory; an existing session never approves it.", "Security", typeof(StepUpRequest), StatusCodes.Status201Created, typeof(StepUpBody), roleChecked: false, scoped: false);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        Results.Json(await proofs.RequestAsync(caller, await this.BodyAsync(StepUpJson.Default.StepUpRequest, cancellationToken), cancellationToken),
            StepUpJson.Default.StepUpBody, statusCode: StatusCodes.Status201Created);
}
