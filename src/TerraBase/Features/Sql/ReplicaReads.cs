using System.Diagnostics.CodeAnalysis;
using TerraBase.Engine;
using TerraBase.Features.Sql.Routing;
using TerraBase.Tenancy;

namespace TerraBase.Features.Sql;

internal static class ReplicaReads
{
    public static async ValueTask<IReplicaReader?> OpenAsync(
        this IDatabaseReplicas replicas, TenantContext database, bool writes, WriteOverride preference, CancellationToken cancellationToken)
    {
        if (writes && preference == WriteOverride.Home)
        {
            return null;
        }

        try
        {
            return await replicas.BeginReadAsync(database, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    public static bool RunsAtHome([NotNullWhen(false)] this IReplicaReader? reader, bool writes, WriteOverride preference) => reader is null || writes || preference == WriteOverride.Home;
}
