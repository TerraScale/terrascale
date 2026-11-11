using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Consumes a dashboard authorization before its irreversible side effects.</summary>
public sealed class ConsumeStepUpEndpoint(StepUpProofs proofs, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/terrascale/step-up/consume");
        this.Describe("consume_step_up", "Consume a dashboard step-up proof", "Atomically consumes an approved, unexpired proof with the current account epoch/state before issuing a PAT, a dashboard plan change or any deletion side effects. ts-dashboard create_pat, ts-auth change_plan, ts-auth delete_organization and ts-dashboard delete_account are accepted. change_plan and delete_organization require organization_id, the organization the caller is about to change, and consume their proof single-use for the signed-in user only when it was minted for that organization; a proof minted for another organization answers 403 and stays unconsumed. create_pat and delete_account refuse organization_id. delete_account requires operation_receipt, a canonical 32-byte base64url deletion receipt; 200 returns operation_grant only after the commit and final five-minute deadline check. Persist it privately: auth DELETE retries require both that secret in X-TerraScale-Deletion-Authorization and the exact receipt/account/state, for 15 days. create_pat, change_plan and delete_organization return 204 without a credential. The organization DELETE then requires the consumed delete_organization proof in X-TerraScale-Step-Up-Proof before the proof's five-minute deadline and spends it. A late or failed deletion consume releases no operation_grant. Other operations consume their ts-auth proofs at the sensitive API itself. Replay or mismatch: 403 step_up_required.", "Security", typeof(ConsumeStepUpRequest), StatusCodes.Status204NoContent, null, roleChecked: false, scoped: false);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<StepUpGrantBody>(StatusCodes.Status200OK);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var body = await this.BodyAsync(StepUpJson.Default.ConsumeStepUpRequest, cancellationToken);
        if (body.Audience == "ts-auth")
        {
            if (body.Purpose is not ("change_plan" or "delete_organization") || body.OperationReceipt is not null || body.OrganizationId is null)
            {
                throw Invalid("Use ts-auth change_plan or delete_organization with its organization_id and without an operation_receipt.");
            }
        }
        else if (body.Audience != "ts-dashboard" || body.Purpose is not ("create_pat" or "delete_account")
            || (body.Purpose == "delete_account") != (body.OperationReceipt is not null) || body.OrganizationId is not null)
        {
            throw Invalid("Use ts-dashboard create_pat, or delete_account with its operation_receipt, without an organization_id.");
        }

        var consumed = await proofs.ConsumeAsync(caller, body.Proof, body.Purpose, body.Audience,
            body.OperationReceipt is null ? null : DeletionReceiptHeader.Hash(body.OperationReceipt), cancellationToken, body.OrganizationId);
        return consumed.OperationGrant is { } grant ? Results.Json(new StepUpGrantBody(grant), StepUpJson.Default.StepUpGrantBody) : Results.NoContent();
    }
}
