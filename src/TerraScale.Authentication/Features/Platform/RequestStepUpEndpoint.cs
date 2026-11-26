using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Features.Platform;

public sealed class RequestStepUpEndpoint(StepUpProofs proofs, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Post("/v1/terrascale/step-up");
        this.Describe("request_step_up", "Start purpose-bound reauthentication", "Creates a pending five-minute proof bound to the account, authentication epoch/state, purpose and audience. Supported: delete_account (ts-auth or ts-dashboard), create_pat (ts-dashboard), delete_organization, start_ownership_transfer, change_plan, rotate_api_key, create_oauth_client, update_oauth_client, rotate_oauth_client, create_webhook_endpoint, rotate_webhook_secret, change_email, add_passkey, delete_passkey and start_impersonation (ts-auth). organization_id is required for, and binds the proof to, delete_organization, start_ownership_transfer and change_plan. For start_impersonation it must be the project ULID, a colon, and the target user ULID. It is refused for every other purpose. Keep proof on the backend; send the browser only to step_up_url. return_uri must be /auth/step-up/callback on a registered dashboard redirect origin, without query or fragment. Fresh primary credentials and active MFA are mandatory; an existing session never approves it.", "Security", typeof(StepUpRequest), StatusCodes.Status201Created, typeof(StepUpBody), scoped: false);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await proofs.RequestAsync(caller, await this.BodyAsync(StepUpJson.Default.StepUpRequest, cancellationToken), cancellationToken) switch
        {
            StepUpBody body => TypedResults.Json(body, StepUpJson.Default.StepUpBody, statusCode: StatusCodes.Status201Created),
            AuthValidation failure => ErrorResults.ToResult(failure),
            AuthQuotaExceeded failure => ErrorResults.ToResult(failure),
            AuthForbidden failure => ErrorResults.ToResult(failure),
        };
}
