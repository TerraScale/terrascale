using Grpc.Core;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

internal sealed class KeyRevocationService(KeyRevocationLocal local) : KeyRevocation.KeyRevocationBase
{
    public override async Task<KeyRevocationReply> Revoke(KeyRevocationRequest request, ServerCallContext context)
    {
        if (!Ulid.TryParse(request.Organization, out _) || !Ulid.TryParse(request.Project, out _) || !Ulid.TryParse(request.Database, out _))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid database scope"));
        }
        var database = new TenantContext(request.Organization, PlanTier.Hobby, request.Project, request.Database);
        return new KeyRevocationReply { Revoked = await local.RevokeAsync(database, context.CancellationToken).ConfigureAwait(false) };
    }
}
