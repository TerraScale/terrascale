using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Consumes a dashboard authorization before its irreversible side effects.</summary>
public sealed class ConsumeStepUpEndpoint(StepUpProofs proofs, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/terrascale/step-up/consume");
        this.Describe("consume_step_up", "Consume a dashboard step-up proof", "Atomically consumes an approved, unexpired proof with the current account epoch/state before issuing a PAT, a dashboard plan change or any deletion side effects. ts-dashboard create_pat, ts-auth change_plan and ts-dashboard delete_account are accepted; change_plan consumes its proof single-use for the signed-in user, and the dashboard binds the resume to the organization the proof was minted for through its one-use encrypted intent. delete_account requires operation_receipt, a canonical 32-byte base64url deletion receipt; 200 returns operation_grant only after the commit and final five-minute deadline check. Persist it privately: auth DELETE retries require both that secret in X-TerraScale-Deletion-Authorization and the exact receipt/account/state, for 15 days. create_pat and change_plan return 204 without a credential. A late or failed deletion consume releases no operation_grant. Other operations consume their ts-auth proofs at the sensitive API itself. Replay or mismatch: 403 step_up_required.", "Security", typeof(ConsumeStepUpRequest), StatusCodes.Status204NoContent, null, roleChecked: false, scoped: false);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<StepUpGrantBody>(StatusCodes.Status200OK);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var body = await this.BodyAsync(StepUpJson.Default.ConsumeStepUpRequest, cancellationToken);
        string? organizationId = null;
        if (body.Audience == "ts-auth")
        {
            if (body.Purpose != "change_plan" || body.OperationReceipt is not null ||
                body.Proof is not { Length: 70 } || body.Proof[26] != '.' ||
                await proofs.ReadAsync(caller, body.Proof[..26], cancellationToken) is not { } pending)
            {
                throw Invalid("Use ts-auth change_plan without an operation_receipt.");
            }

            organizationId = pending.Record.OrganizationId ?? throw StepUpProofs.Required();
        }
        else if (body.Audience != "ts-dashboard" || body.Purpose is not ("create_pat" or "delete_account")
            || (body.Purpose == "delete_account") != (body.OperationReceipt is not null))
        {
            throw Invalid("Use ts-dashboard create_pat, or delete_account with its operation_receipt.");
        }

        var consumed = await proofs.ConsumeAsync(caller, body.Proof, body.Purpose, body.Audience,
            body.OperationReceipt is null ? null : DeletionReceiptHeader.Hash(body.OperationReceipt), cancellationToken, organizationId);
        return consumed.OperationGrant is { } grant ? Results.Json(new StepUpGrantBody(grant), StepUpJson.Default.StepUpGrantBody) : Results.NoContent();
    }
}
