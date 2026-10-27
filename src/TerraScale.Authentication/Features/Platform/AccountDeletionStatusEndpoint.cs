using System.Security.Cryptography;
using System.Text;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Status-only proof after the account's sign-in credentials stop working.</summary>
public sealed class AccountDeletionStatusEndpoint(AccountDeletion accounts) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
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

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var hash = DeletionReceiptHeader.Read(this.HttpContext.Request, required: true)!;
            var query = this.HttpContext.Request.Query;
            string? organization = null;
            if (query.TryGetValue("organization", out var values))
            {
                if (values.Count != 1 || !PlatformDirectory.IsId(values[0]!))
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "invalid_request", "organization must be one canonical ULID.");
                }

                organization = values[0];
            }

            return await accounts.StatusAsync(hash, organization, cancellationToken) ? Results.NoContent() : Results.Accepted();
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            throw ApiException.Unavailable(error);
        }
    }
}

internal static class DeletionReceiptHeader
{
    internal const string Name = "X-TerraScale-Deletion-Receipt";

    internal static string? Read(HttpRequest request, bool required = false)
    {
        if (!request.Headers.TryGetValue(Name, out var values) && !required)
        {
            return null;
        }

        return values.Count == 1 ? Hash(values[0]) : throw new ApiException(400, "invalid_request", "Present one deletion receipt.");
    }

    internal static string Hash(string? value)
    {
        if (value is not { Length: 43 }
            || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            || !"AEIMQUYcgkosw048".Contains(value[^1]))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_request", "Present one canonical 32-byte base64url deletion receipt.");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
    }
}
