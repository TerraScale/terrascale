using System.Globalization;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

internal readonly union AuthorizedOrganization(Organization, NotFound, Forbidden);

internal sealed partial class EngineManagementService
{
    internal static async ValueTask<long> NextSequenceAsync(IStorageTransaction transaction, string collection, string counter, CancellationToken cancellationToken)
    {
        var next = await transaction.GetAsync(collection, counter, cancellationToken).ConfigureAwait(false) is { } current
            ? JsonSerializer.Deserialize(current.Json.Span, TerraBaseJsonContext.Default.Int64) + 1
            : 0;
        await PutAsync(transaction, collection, counter, next, TerraBaseJsonContext.Default.Int64, cancellationToken).ConfigureAwait(false);
        return next;
    }

    internal static string AscendingKey(long sequence) => sequence.ToString("D19", CultureInfo.InvariantCulture);

    internal static string TicksKey(DateTimeOffset time) => AscendingKey(time.UtcTicks);

    internal static string State(string organizationId) => $"organizations/{organizationId}/state";

    private static string PlanName(TerraBase.Tenancy.PlanTier plan) => plan.ToString().ToLowerInvariant();

    private static async ValueTask<AuthorizedOrganization> AuthorizeAsync(
        IStorageTransaction transaction, string userId, string organizationId, bool allowProject, CancellationToken cancellationToken)
    {
        var organization = await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);
        if (organization is null || organization.CreatedBy != userId && !(allowProject && ProjectActor(userId, organizationId)))
        {
            return ManagementErrors.NotFound;
        }

        return organization;
    }
}
