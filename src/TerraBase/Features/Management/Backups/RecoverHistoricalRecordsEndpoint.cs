using TerraBase.Features.Documents;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary>Restores only the approved historical records into their original database.</summary>
public sealed class RecoverHistoricalRecordsEndpoint(ISelectiveRecovery recovery) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Database + "/point-in-time/recover");
        Name("recoverHistoricalRecords");
        Summary("Atomically restores explicitly approved historical records");
        Description(SelectiveRecoveryRoute.Description);
        Metadata(new RequestBodyMetadata(typeof(RecoveryRequest), DocumentRoute.JsonMediaType));
        Produces<TransactionBody>();
        SelectiveRecoveryRoute.Configure(Definition, ApiKeyScopes.DocumentsWrite);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => recovery.RecoverAsync(HttpContext, cancellationToken);
}
