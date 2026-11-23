using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Platform;

internal sealed record AccountDeletionHistory(string ErasureId, DateTimeOffset RequestedAt, DateTimeOffset? AuthLogicalVerifiedAt,
    string? HistoryStatus, DateTimeOffset? HistoryCompletedAt);

public sealed class AccountDeletionHistoryEndpoint(AccountDeletion accounts) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/terrascale/account/deletion/history");
        Name("account_deletion_history");
        Summary("Read your deletion's history request metadata");
        Description("Requires the existing generation-specific deletion receipt capability. Optional organization ULID must be a candidate covered by that receipt. Returns only the opaque erasure id, immutable request time and auth worker progress after logical deletion; 404 when no history job is available (including retirement), never completion evidence. Does not advance cleanup, settlement or erasure and returns no subject ids, records or tokens. Read the peer-only TerraBase inspection separately for holder metadata.");
        Tags("Members");
        Produces<AccountDeletionHistory>();
        foreach (var status in new[] { 400, 404, 409, 503 })
        {
            Produces<ErrorBody>(status);
        }

        Metadata(new RequiresSecurity(SecuritySchemes.AccountDeletionReceipt));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            return await (DeletionReceiptHeader.Read(this.HttpContext.Request, required: true) switch
            {
                AuthValidation missing => ValueTask.FromResult(ErrorResults.ToResult(missing)),
                string receipt => new ValueTask<IResult>(this.HistoryAsync(receipt, cancellationToken)),
            });
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            throw ApiException.Unavailable(error);
        }
    }

    private async Task<IResult> HistoryAsync(string hash, CancellationToken cancellationToken)
    {
        var query = this.HttpContext.Request.Query;
        string? organization = null;
        if (query.TryGetValue("organization", out var values))
        {
            if (values.Count != 1 || !PlatformDirectory.IsId(values[0]!))
            {
                return ErrorResults.ToResult(new AuthValidation("organization must be one canonical ULID."));
            }

            organization = values[0];
        }

        return await accounts.HistoryAsync(hash, organization, cancellationToken) switch
        {
            AccountDeletionHistory history => TypedResults.Json(history, DeletionHistoryJson.Default.AccountDeletionHistory),
            AuthNotFound failure => ErrorResults.ToResult(failure),
            AuthConflict failure => ErrorResults.ToResult(failure),
        };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AccountDeletionHistory))]
internal sealed partial class DeletionHistoryJson : JsonSerializerContext;
