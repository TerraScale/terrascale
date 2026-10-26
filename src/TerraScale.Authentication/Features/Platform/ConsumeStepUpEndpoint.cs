using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Consumes a dashboard authorization before its irreversible side effects.</summary>
public sealed class ConsumeStepUpEndpoint(StepUpProofs proofs, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/terrascale/step-up/consume");
        this.Document("consume_step_up", "Consume a dashboard step-up proof", "Atomically consumes an approved, unexpired proof with the current account epoch/state before issuing a PAT or any deletion side effects. Only ts-dashboard create_pat and delete_account are accepted. delete_account requires operation_receipt, a canonical 32-byte base64url deletion receipt; 200 returns operation_grant only after the commit and final five-minute deadline check. Persist it privately: auth DELETE retries require both that secret in X-TerraScale-Deletion-Authorization and the exact receipt/account/state, for 15 days. create_pat returns 204 without a credential. A late or failed deletion consume releases no operation_grant. All other operations consume their ts-auth proofs at the sensitive API itself. Replay or mismatch: 403 step_up_required.", "Security", typeof(ConsumeStepUpRequest), StatusCodes.Status204NoContent, null, roleChecked: false, scoped: false);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<StepUpGrantBody>(StatusCodes.Status200OK);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var body = await this.BodyAsync(StepUpJson.Default.ConsumeStepUpRequest, cancellationToken);
        if (body.Audience != "ts-dashboard" || body.Purpose is not ("create_pat" or "delete_account")
            || (body.Purpose == "delete_account") != (body.OperationReceipt is not null))
        {
            throw Invalid("Use ts-dashboard create_pat, or delete_account with its operation_receipt.");
        }

        var consumed = await proofs.ConsumeAsync(caller, body.Proof, body.Purpose, body.Audience,
            body.OperationReceipt is null ? null : DeletionReceiptHeader.Hash(body.OperationReceipt), cancellationToken);
        return consumed.OperationGrant is { } grant ? Results.Json(new StepUpGrantBody(grant), StepUpJson.Default.StepUpGrantBody) : Results.NoContent();
    }
}
