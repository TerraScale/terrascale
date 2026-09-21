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

internal sealed class GetSchemasEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/scim/{directoryId}/Schemas", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string directoryId, HttpRequest request, AuthStore store, CancellationToken ct)
    {
        var gate = await RequireDirectoryAsync(request, store, directoryId, ct);
        if (gate.Failure is { } failure)
            return failure;

        return ScimJson(new ScimListResponse(
            Schemas: [ListResponseSchema],
            TotalResults: 2,
            StartIndex: 1,
            ItemsPerPage: 2,
            Resources:
            [
                new
                {
                    schemas = new[] { SchemaSchema },
                    id = UserSchema,
                    name = "User",
                    attributes = SchemaAttributes(
                        ("id", "string", ReadOnly: true),
                        ("userName", "string", ReadOnly: false),
                        ("active", "boolean", ReadOnly: false),
                        ("externalId", "string", ReadOnly: false),
                        ("name", "complex", ReadOnly: false),
                        ("meta", "complex", ReadOnly: true)),
                },
                new
                {
                    schemas = new[] { SchemaSchema },
                    id = GroupSchema,
                    name = "Group",
                    attributes = SchemaAttributes(
                        ("id", "string", ReadOnly: true),
                        ("displayName", "string", ReadOnly: false),
                        ("members", "complex", ReadOnly: false),
                        ("meta", "complex", ReadOnly: true)),
                },
            ]));
    }

    private const string SchemaSchema = "urn:ietf:params:scim:schemas:core:2.0:Schema";

    private static object[] SchemaAttributes(params (string Name, string Type, bool ReadOnly)[] attributes) =>
        [.. attributes.Select(a => (object)new
        {
            name = a.Name,
            type = a.Type,
            multiValued = false,
            required = false,
            mutability = a.ReadOnly ? "readOnly" : "readWrite",
            returned = "default",
        })];
}
