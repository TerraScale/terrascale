using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.S3;
using Microsoft.OpenApi;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Documents;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Shapes;
internal sealed class GetShapeKeyEndpoint(
    IDocumentService documents, ITenantDirectory tenants, IShapeLineages lineages, IShapeCrypto crypto, ICatalogGate catalog, IApiKeyStore keys,
    TimeProvider time) : EndpointWithoutRequest<IResult>
{
    private static readonly Unavailable CatalogStale = new("this machine cannot confirm current credentials; retry later", "catalog_stale");

    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    public override void Configure()
    {
        Get(DocumentRoute.Collections + "/{collection}/shape/keys");
        Name("getShapeKey");
        Summary("Gets a shape epoch key");
        Description("Requires documents:read. End-user tokens cannot retrieve epoch keys; use the origin shape endpoint. Releases the AES-256 epoch key for the current handle and a served epoch/key_id pair. The key opens only that handle and epoch. Epochs last 10 minutes. Future epochs are refused; stale handles and unretained pairs answer 409 must_refetch. Responses are private, no-store. API key revocation prevents further key release; offline JWT acceptance lasts until expiry plus clock skew. A catalog whose local restore anchor is unverified answers 503 catalog_stale and releases nothing. The API key is revalidated against the local catalog immediately before key release. Previously released keys and read data remain usable.");
        Tags("Live");
        Produces<ShapeKeyResponse>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ShapeErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(Query("handle", "The current shape handle: h1- and 16 lowercase hex characters."),
            Query("epoch", "Canonical unsigned decimal epoch, floor(unix_seconds / 600)."),
            Query("key_id", "Canonical uppercase ULID of the project data key named by the TBS1 header."));
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(new ListenerMetadata(TerraBaseListener.Client), OriginNoStore.Instance);
    }

    private static OpenApiParameter Query(string name, string description) => new()
    {
        Name = name,
        In = ParameterLocation.Query,
        Description = description,
        Required = true,
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (CollectionRuleAccess.EndUser)
        {
            return ErrorResponse.From(ShapeAccess.Denied);
        }

        if (!catalog.IsOpen)
        {
            return ErrorResponse.From(CatalogStale);
        }

        return await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => DocumentRoute.CheckCollectionName(HttpContext) switch
            {
                Validation error => ErrorResponse.From(error),
                string name => await documents.GetCollectionAsync(database, name, cancellationToken) switch
                {
                    NotFound error => ErrorResponse.From(error),
                    Conflict error => ErrorResponse.From(error),
                    Unavailable error => ErrorResponse.From(error),
                    CollectionInfo collection => await ServeAsync(database, collection, cancellationToken),
                },
            },
        };
    }

    private async ValueTask<IResult> ServeAsync(TenantContext database, CollectionInfo collection, CancellationToken cancellationToken)
    {
        var query = HttpContext.Request.Query;
        if (query.Count != 3 || !query.TryGetValue("handle", out var handles) || handles.Count != 1
            || !query.TryGetValue("epoch", out var epochs) || epochs.Count != 1
            || !query.TryGetValue("key_id", out var ids) || ids.Count != 1
            || handles[0] is not { Length: 19 } handle || !handle.StartsWith("h1-", StringComparison.Ordinal)
            || handle.AsSpan(3).ContainsAnyExcept(LowerHex)
            || !long.TryParse(epochs[0], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            || epoch < 0 || epoch.ToString(CultureInfo.InvariantCulture) != epochs[0] || epoch > crypto.CurrentEpoch
            || !EntityIds.IsValid(ids[0]))
        {
            return ErrorResponse.From(new Validation("handle, epoch and key_id must be canonical, single values; future epochs are not served"));
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var resolved = await lineages.ResolveHandleAsync(database, collection, handle, linked.Token);
            if (resolved is not ShapeLineage lineage)
            {
                return resolved switch
                {
                    Evicted evicted => Refetch(evicted.Lineage, "the filtered shape was evicted"),
                    QuotaExceeded refused => ErrorResponse.From(refused),
                    Forbidden denied => ErrorResponse.From(denied),
                    NotFound missing => ErrorResponse.From(missing),
                    Unavailable unavailable => ErrorResponse.From(unavailable),
                    ShapeLineage => throw new System.Diagnostics.UnreachableException(),
                };
            }

            if (lineage.Handle != handle)
            {
                return Refetch(lineage, "shape lineage changed");
            }

            var key = await crypto.GetKeyAsync(database, handle, epoch, ids[0]!, linked.Token);
            if (key is null)
            {
                return Refetch(lineage, "the epoch and data key pair is not retained");
            }

            try
            {
                if (!catalog.IsOpen)
                {
                    return ErrorResponse.From(CatalogStale);
                }
                if (HttpContext.User.Identity?.AuthenticationType == ApiKeyAuthenticationHandler.SchemeName
                    && (ApiKeyAuthenticationHandler.Secret(HttpContext.Request) is not { } secret
                        || await keys.FindAsync(secret, cancellationToken) is not ApiKey current || current.Id != ApiKey.Of(HttpContext).Id))
                {
                    return ErrorResponse.From(new Unauthorized("The request cannot be served.", "invalid_key"));
                }

                return TypedResults.Json(new ShapeKeyResponse(handle, epoch.ToString(CultureInfo.InvariantCulture), key.KeyId,
                    Base64Url.EncodeToString(key.Key)), ShapeKeyJsonContext.Default.ShapeKeyResponse);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key.Key);
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested
            && error is IOException or HttpRequestException or AmazonS3Exception or InvalidDataException or OperationCanceledException)
        {
            return ErrorResponse.From(new Unavailable("the project data key is unavailable"));
        }
    }

    private Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ShapeErrorBody> Refetch(ShapeLineage lineage, string message)
    {
        HttpContext.Response.Headers["TB-Shape-Handle"] = lineage.Handle;
        return TypedResults.Json(new ShapeErrorBody(new ShapeErrorDetail("must_refetch", message, lineage.Handle)),
            ShapeJsonContext.Default.ShapeErrorBody, statusCode: StatusCodes.Status409Conflict);
    }
}
internal sealed record ShapeKeyResponse(string Handle, string Epoch, string KeyId, string Key);
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ShapeKeyResponse))]
internal sealed partial class ShapeKeyJsonContext : JsonSerializerContext;
