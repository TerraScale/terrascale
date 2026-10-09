using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Indexes;

/// <summary><c>POST …/databases/{database}/indexes</c>: builds a secondary index.</summary>
public sealed class CreateIndexEndpoint(IManagementService management, IIndexService indexes) : Endpoint<CreateIndexRequest, IResult>
{
    private const int MaxFields = 4;

    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Indexes);
        Name("createIndex");
        Summary("Builds a secondary index");
        Description("collection: a collection name. fields: 1 to 4 distinct dotted member paths (not _id or _rev, which the primary key already orders). name (optional): a lowercase letter, then lowercase letters, digits or _, at most 63 characters; derived from the collection and fields when omitted. The index is built before the response." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Indexes");
        Produces<IndexBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CreateIndexRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IndexOf(request) switch
        {
            Validation error => ErrorResponse.From(error),
            IndexDefinition index => await ManagementRoute.CheckWritableDatabaseAsync(HttpContext, management, cancellationToken) switch
            {
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
                TenantContext database => await indexes.CreateAsync(database, index, cancellationToken) switch
                {
                    IndexBody created => TypedResults.Created($"{ManagementRoute.DatabasePath(HttpContext, database)}/indexes/{index.Name}", created),
                    NotFound error => ErrorResponse.From(error),
                    Conflict error => ErrorResponse.From(error),
                },
            },
        };
    }

    private static Valid<IndexDefinition> IndexOf(CreateIndexRequest request)
    {
        if (!DatabaseName.TryParse(request.Collection, out var collection))
        {
            return new Validation("collection must be a lowercase name of at most 63 characters");
        }

        // The count is checked before any field is parsed, so an oversized list is rejected without parsing its paths.
        if (request.Fields is not { Count: >= 1 and <= MaxFields } raws)
        {
            return new Validation("fields must hold 1 to 4 distinct fields");
        }

        var fields = new List<DocumentField>(raws.Count);
        foreach (var raw in raws)
        {
            if (!DocumentField.TryParse(raw, out var field) || field.IsMetadata)
            {
                return new Validation("fields must be dotted member paths of letters, digits and _, not _id or _rev");
            }

            fields.Add(field);
        }

        if (fields.Distinct().Count() != fields.Count)
        {
            return new Validation("fields must hold 1 to 4 distinct fields");
        }

        var name = request.Name ?? IndexService.DefaultName(collection.Value, fields);
        return IndexService.IsValidName(name)
            ? new IndexDefinition(name, collection.Value, fields)
            : new Validation("name must be a lowercase letter, then lowercase letters, digits or _, at most 63 characters");
    }
}
