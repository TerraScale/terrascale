using TerraScale.Endpoints;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Indexes;

/// <summary><c>POST …/databases/{database}/indexes</c>: builds a secondary index.</summary>
public sealed class CreateIndexEndpoint(IManagementService management, IIndexService indexes) : Endpoint<CreateIndexRequest, IResult>
{
    private const int MaxFields = 4;

    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Indexes);
        Name("createIndex");
        Summary("Builds a secondary index");
        Description("collection: a collection name. fields: 1 to 4 distinct dotted member paths (not _id or _rev, which the primary key already orders). name (optional): a lowercase letter, then lowercase letters, digits or _, at most 63 characters; derived from the collection and fields when omitted. The index is built before the response." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<IndexBody>(StatusCodes.Status201Created);
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CreateIndexRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var index = IndexOf(request);
        if (await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is not { } database)
        {
            return TypedResults.NotFound();
        }

        var result = await indexes.CreateAsync(database, index, cancellationToken);
        return result.Error switch
        {
            ManagementError.None => TypedResults.Created($"{ManagementRoute.DatabasePath(database)}/indexes/{index.Name}", result.Value),
            ManagementError.NameTaken => TypedResults.Conflict("an index with this name, or on these fields of the collection, already exists"),
            _ => ManagementRoute.Error(result.Error),
        };
    }

    private static IndexDefinition IndexOf(CreateIndexRequest request)
    {
        if (!DatabaseName.TryParse(request.Collection, out var collection))
        {
            throw new BadHttpRequestException("collection must be a lowercase name of at most 63 characters");
        }

        // The count is checked before any field is parsed, so an oversized list is rejected without parsing its paths.
        if (request.Fields is not { Count: >= 1 and <= MaxFields } raws)
        {
            throw new BadHttpRequestException("fields must hold 1 to 4 distinct fields");
        }

        var fields = new List<DocumentField>(raws.Count);
        foreach (var raw in raws)
        {
            fields.Add(DocumentField.TryParse(raw, out var field) && !field.IsMetadata
                ? field
                : throw new BadHttpRequestException("fields must be dotted member paths of letters, digits and _, not _id or _rev"));
        }

        if (fields.Distinct().Count() != fields.Count)
        {
            throw new BadHttpRequestException("fields must hold 1 to 4 distinct fields");
        }

        var name = request.Name ?? IndexService.DefaultName(collection.Value, fields);
        return IndexService.IsValidName(name)
            ? new IndexDefinition(name, collection.Value, fields)
            : throw new BadHttpRequestException("name must be a lowercase letter, then lowercase letters, digits or _, at most 63 characters");
    }
}
