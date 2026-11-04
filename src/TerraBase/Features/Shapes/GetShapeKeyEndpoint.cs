using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.S3;
using Microsoft.OpenApi;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Shapes;

internal sealed class GetShapeKeyEndpoint(
    ITenantDirectory tenants, IShapeLineages lineages, IShapeCrypto crypto, ICatalogGate catalog, IApiKeyStore keys,
    TimeProvider time) : EndpointWithoutRequest<IResult>
{
    private static readonly Unavailable CatalogStale = new("this machine cannot confirm current credentials; retry later", "catalog_stale");

    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    public override void Configure()
    {
        Get(ShapeRoute.Shape + "/keys");
        Name("getShapeKey");
        Summary("Gets a shape epoch key");
        Description("Requires documents:read. Releases the AES-256 epoch key for the current handle and a served epoch/key_id pair. The key opens only that handle and epoch. Epochs last 10 minutes. Future epochs are refused; stale handles and unretained pairs answer 409 must_refetch. Responses are private, no-store. API key revocation prevents further key release; offline JWT acceptance lasts until expiry plus clock skew. A catalog whose local restore anchor is unverified answers 503 catalog_stale and releases nothing. The API key is revalidated against the local catalog immediately before key release. Previously released keys and read data remain usable.");
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
        if (!catalog.IsOpen)
        {
            return ErrorResponse.From(CatalogStale);
        }

        var resolved = await ShapeRoute.ResolveAsync(HttpContext, tenants, lineages, cancellationToken);
        return resolved is ResolvedShape shape ? await ServeAsync(shape, cancellationToken) : ShapeRoute.Error(resolved);
    }

    private async ValueTask<IResult> ServeAsync(ResolvedShape shape, CancellationToken cancellationToken)
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
            var (database, lineage) = (shape.Scope.Database, shape.Lineage);
            if (lineage.Handle != handle)
            {
                return ShapeRoute.Refetch(HttpContext, lineage, "shape lineage changed");
            }

            var key = await crypto.GetKeyAsync(database, handle, epoch, ids[0]!, linked.Token);
            if (key is null)
            {
                return ShapeRoute.Refetch(HttpContext, lineage, "the epoch and data key pair is not retained");
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
}
internal sealed record ShapeKeyResponse(string Handle, string Epoch, string KeyId, string Key);
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ShapeKeyResponse))]
internal sealed partial class ShapeKeyJsonContext : JsonSerializerContext;
