namespace TerraScale.Auth.Scim;

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TerraScale.Auth.Api;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Scim.ScimSupport;

internal sealed class ListGroupsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/scim/{directoryId}/Groups", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, HttpRequest request, AuthStore store, TerraScaleClient client, CancellationToken ct,
            string? filter, int? count, int? startIndex)
    {
        var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
        if (gate.Failure is { } failure)
            return failure;
        var directory = gate.Directory!;

        if (filter is not null)
            return ScimError(400, "filters are not supported on the Groups endpoint", "invalidFilter");
        var (offset, pageSize) = Paginate(startIndex, count);

        var groups = new ScimGroups(client, store.Database);
        var ordered = (await groups.ListByDirectoryAsync(directory.Id, ct)).OrderBy(g => g.Id, StringComparer.Ordinal).ToList();
        var resources = ordered.Skip(offset - 1).Take(pageSize).Select(g => GroupView(directory.Id, g)).ToList();
        return ScimJson(new ScimListResponse(
            Schemas: [ListResponseSchema],
            TotalResults: ordered.Count,
            StartIndex: offset,
            ItemsPerPage: resources.Count,
            Resources: resources));
    }
}
