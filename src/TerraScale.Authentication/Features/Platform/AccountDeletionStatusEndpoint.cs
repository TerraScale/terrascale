using System.Security.Cryptography;
using System.Text;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Platform;

public sealed class AccountDeletionStatusEndpoint(AccountDeletion accounts) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Get("/v1/terrascale/account/deletion");
        Name("account_deletion_status");
        Summary("Read your account deletion status");
        Description("Present the deletion receipt capability header, without a bearer token. 204 proves logical account deletion for its exact closure generation; 202 means that generation is pending. 404 unknown or expired; 409 deletion_refused when the generation ended without completion proof. Optional organization ULID query: 204 requires both account proof and that candidate organization’s deletion fence; a surviving candidate stays 202, noncandidates are 404. Grants no mutation or personal data. Does not certify physical erasure or other services' data.");
        Tags("Members");
        Produces(StatusCodes.Status204NoContent);
        Produces(StatusCodes.Status202Accepted);
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
                string receipt => new ValueTask<IResult>(this.StatusAsync(receipt, cancellationToken)),
            });
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            throw ApiException.Unavailable(error);
        }
    }

    private async Task<IResult> StatusAsync(string hash, CancellationToken cancellationToken)
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

        return await accounts.StatusAsync(hash, organization, cancellationToken) switch
        {
            true => TypedResults.NoContent(),
            false => TypedResults.Accepted(default(string)),
            AuthNotFound failure => ErrorResults.ToResult(failure),
            AuthConflict failure => ErrorResults.ToResult(failure),
        };
    }
}

internal readonly union DeletionReceiptRead(string, AuthValidation);

internal static class DeletionReceiptHeader
{
    internal const string Name = "X-TerraScale-Deletion-Receipt";

    internal static DeletionReceiptRead Read(HttpRequest request, bool required = false)
    {
        if (!request.Headers.TryGetValue(Name, out var values) && !required)
        {
            return string.Empty;
        }

        return values.Count == 1 ? Hash(values[0]) : new AuthValidation("Present one deletion receipt.");
    }

    internal static DeletionReceiptRead Hash(string? value)
    {
        if (value is not { Length: 43 }
            || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            || !"AEIMQUYcgkosw048".Contains(value[^1]))
        {
            return new AuthValidation("Present one canonical 32-byte base64url deletion receipt.");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
    }
}
