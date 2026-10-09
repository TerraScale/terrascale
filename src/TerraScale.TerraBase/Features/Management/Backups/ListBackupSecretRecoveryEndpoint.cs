using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary>Lists uncertain create metadata for a loopback operator.</summary>
public sealed class ListBackupSecretRecoveryEndpoint(IBackupSecretRecovery recovery) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/backup-secret-lifecycle");
        Name("listBackupSecretRecovery");
        Summary("List uncertain backup credential creates");
        Description("Loopback management service credential only. Reads eight metadata-only intents; after is the previous page's next ULID. No credentials are returned.");
        Tags("Backups");
        Produces<BackupSecretRecoveryPage>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Admin);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!HttpContext.User.IsManagementService())
        {
            return ErrorResponse.From(new Forbidden("backup secret recovery needs the management service credential"));
        }

        var after = HttpContext.Request.Query["after"].ToString();
        if (after.Length != 0 && !Ulid.TryParse(after, out _))
        {
            return ErrorResponse.From(new Validation("after must be a ULID"));
        }

        return TypedResults.Ok(await recovery.ListAsync(after.Length == 0 ? null : after, cancellationToken));
    }
}
