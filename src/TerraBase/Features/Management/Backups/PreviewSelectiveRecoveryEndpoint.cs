using TerraBase.Features.Documents;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary>Previews actual retained historical records without publishing a customer database.</summary>
public sealed class PreviewSelectiveRecoveryEndpoint(ISelectiveRecovery recovery) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Database + "/point-in-time/recovery-preview");
        Name("previewSelectiveRecovery");
        Summary("Previews selected historical records against their current contents");
        Description(SelectiveRecoveryRoute.Description);
        Metadata(new RequestBodyMetadata(typeof(RecoveryPreviewRequest), DocumentRoute.JsonMediaType));
        Produces<RecoveryPreview>();
        SelectiveRecoveryRoute.Configure(Definition, ApiKeyScopes.DocumentsRead);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => recovery.PreviewAsync(HttpContext, cancellationToken);
}
