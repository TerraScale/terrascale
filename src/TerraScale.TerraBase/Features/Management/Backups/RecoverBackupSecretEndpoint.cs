using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary>Releases a fenced uncertain create after explicit operator investigation.</summary>
public sealed class RecoverBackupSecretEndpoint(IBackupSecretRecovery recovery) : Endpoint<BackupSecretRecoveryRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/backup-secret-lifecycle/{intent}/confirm-absent");
        Name("confirmBackupSecretAbsent");
        Summary("Confirm an uncertain backup credential was never created");
        Description("Loopback management service credential only. Before calling, verify the exact secretName in the configured OCI vault/compartment and investigate outstanding create requests. confirmAbsent=true attests no secret or pending create exists. A fresh metadata lookup must also be empty. Only unknown, already-fenced intents may be removed; published or known-ID work is refused. Incorrect operator confirmation may leave a late remote secret untracked.");
        Tags("Backups");
        Metadata(new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Admin);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(BackupSecretRecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HttpContext.User.IsManagementService())
        {
            return ErrorResponse.From(new Forbidden("backup secret recovery needs the management service credential"));
        }

        var id = HttpContext.Request.RouteValues["intent"]?.ToString();
        if (!Ulid.TryParse(id, out _) || !request.ConfirmAbsent || request.SecretName != "terrabase-backup-" + id)
        {
            return ErrorResponse.From(new Validation("a valid intent ULID, matching secretName and confirmAbsent=true are required"));
        }

        try
        {
            return await recovery.ConfirmAbsentAsync(id!, request.SecretName, cancellationToken) switch
            {
                true => TypedResults.NoContent(),
                false => ErrorResponse.From(new Conflict("the intent is not an absent, fenced uncertain create")),
                Unavailable error => ErrorResponse.From(error),
            };
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            return ErrorResponse.From(new Unavailable("backup secret recovery could not be verified"));
        }
    }
}
