using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Opaque history correlation; never a physical-erasure certificate.</summary>
internal sealed record AccountDeletionHistory(string ErasureId, DateTimeOffset RequestedAt, DateTimeOffset? AuthLogicalVerifiedAt,
    string? HistoryStatus, DateTimeOffset? HistoryCompletedAt);

/// <summary>Reads one receipt-authorized history job without exposing the subject or its records.</summary>
public sealed class AccountDeletionHistoryEndpoint(AccountDeletion accounts) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
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

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            return await (DeletionReceiptHeader.Read(this.HttpContext.Request, required: true) switch
            {
                Validation missing => ValueTask.FromResult(ErrorResults.ToResult(missing)),
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
                return ErrorResults.ToResult(new Validation("organization must be one canonical ULID."));
            }

            organization = values[0];
        }

        return await accounts.HistoryAsync(hash, organization, cancellationToken) is { } history
            ? Results.Json(history, DeletionHistoryJson.Default.AccountDeletionHistory) : ErrorResults.ToResult(PlatformDirectory.NotFoundFailure());
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AccountDeletionHistory))]
internal sealed partial class DeletionHistoryJson : JsonSerializerContext;
