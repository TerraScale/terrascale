using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary>Public identity fields only; credentials and authentication state are never serialized.</summary>
public sealed record DirectoryUserBody(string Id, string Email, bool EmailVerified, DateTimeOffset CreatedAt, bool Disabled, IReadOnlyList<DirectoryProviderBody> LinkedProviders);

/// <summary>A verified public provider identity, never provider credentials.</summary>
public sealed record DirectoryProviderBody(string Provider, string ClientId, string Subject);

/// <summary>A bounded directory page with a creation cutoff, not a transaction snapshot.</summary>
public sealed record DirectoryPageBody(IReadOnlyList<DirectoryUserBody> Items, string? Next, DateTimeOffset CreatedBefore);

internal sealed record DirectoryCursor(DateTimeOffset CreatedBefore, string Position);

/// <summary><c>GET /v1/{project}/users/export</c>.</summary>
public sealed class ExportProjectUsersEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, AuthStore store, PlatformDirectory platform, TimeProvider clock)
    : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Root + "/users/export");
        this.Document("export_project_users", "Export an end-user directory page", "Needs admin on every page. limit 1..100 (default 100); after is the previous next; format json (default) or csv. X-Export-Next carries the continuation for either format; absent means complete. X-Export-Created-Before is the fixed creation cutoff. Immutable storage-id order, not ULID order. Later creations are excluded; deletions disappear and profile fields are read per page, not a transaction snapshot. Retry the same cursor to resume; retain each page once. Replication lag and creator clock skew can omit accounts; retries may reflect changed fields, so deduplicate by id. Email possession was proven at account creation; only email, linked provider/client/subject and public profile fields are exported; no credentials or sessions are portable. Every returned page is audited.", "Project tenants", null, StatusCodes.Status200OK, typeof(DirectoryPageBody), paged: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var (limit, after) = this.Paging(100);
        var format = this.HttpContext.Request.Query["format"].ToString();
        if (format is not ("" or "json" or "csv"))
        {
            throw Invalid("format must be json or csv.");
        }

        var cutoff = clock.GetUtcNow();
        string? position = null;
        if (after is not null)
        {
            try
            {
                var cursor = after.Length <= 8192 ? JsonSerializer.Deserialize(WebEncoders.Base64UrlDecode(after), DirectoryExportJson.Default.DirectoryCursor) : null;
                if (cursor is null || string.IsNullOrEmpty(cursor.Position))
                {
                    throw new FormatException();
                }

                (cutoff, position) = (cursor.CreatedBefore, cursor.Position);
            }
            catch (Exception error) when (error is FormatException or JsonException)
            {
                throw Invalid("after must be a cursor from this export.");
            }
        }

        TerraBaseDocumentPage page;
        try
        {
            page = await store.PageAsync(AuthStore.UserCollection, projectId,
                [new JsonObject { ["lt"] = new JsonArray("/createdAt", JsonValue.Create(cutoff)) }], [], limit, position, cancellationToken: cancellationToken);
        }
        catch (TerraBaseException error) when (error.ErrorCode == "invalid_cursor")
        {
            throw Invalid("after must be a cursor from this project's export.");
        }

        var items = page.Items.Select(document => document.Document.Deserialize(IdentityJson.Default.UserRecord))
            .Where(user => user is not null && user.InstanceId == projectId)
            .Select(user => new DirectoryUserBody(user!.Id, user.Email, true, user.CreatedAt, user.Disabled,
                [.. user.LinkedProviders.Select(link => new DirectoryProviderBody(link.Provider, link.ClientId, link.Subject))])).ToArray();
        var next = page.NextCursor is { } more ? WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new DirectoryCursor(cutoff, more), DirectoryExportJson.Default.DirectoryCursor)) : null;
        var project = await platform.ProjectAsync(projectId, cancellationToken) ?? throw PlatformDirectory.NotFound();
        if (await store.TransactAsync([platform.Audit(project.Value.OrganizationId, caller.Id, "project.users_exported", projectId,
            $"{(format == "csv" ? "csv" : "json")}/{items.Length.ToString(CultureInfo.InvariantCulture)}")], cancellationToken) is not null)
        {
            throw ApiException.Unavailable();
        }

        this.HttpContext.Response.Headers["X-Export-Created-Before"] = cutoff.ToString("O", CultureInfo.InvariantCulture);
        this.HttpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{projectId}-users.{(format == "csv" ? "csv" : "json")}\"";
        if (next is not null)
        {
            this.HttpContext.Response.Headers["X-Export-Next"] = next;
        }

        return format == "csv" ? Results.Text(Csv(items), "text/csv", Encoding.UTF8)
            : Results.Json(new DirectoryPageBody(items, next, cutoff), DirectoryExportJson.Default.DirectoryPageBody);
    }

    private static string Csv(IEnumerable<DirectoryUserBody> items)
    {
        var csv = new StringBuilder("id,email,email_verified,created_at,disabled,linked_providers\r\n");
        foreach (var user in items)
        {
            csv.AppendJoin(',', new[] { user.Id, user.Email, "true", user.CreatedAt.ToString("O", CultureInfo.InvariantCulture), user.Disabled ? "true" : "false",
                JsonSerializer.Serialize(user.LinkedProviders, DirectoryExportJson.Default.IReadOnlyListDirectoryProviderBody) }.Select(Cell)).Append("\r\n");
        }

        return csv.ToString();
    }

    // Quoting alone does not prevent spreadsheet formulas. Prefix dangerous leading characters, then RFC 4180 quote.
    private static string Cell(string value) => "\"" + ((value.Length > 0 && "'=+-@\t\r\n".Contains(value[0], StringComparison.Ordinal) ? "'" : string.Empty) + value).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DirectoryPageBody))]
[JsonSerializable(typeof(DirectoryCursor))]
[JsonSerializable(typeof(IReadOnlyList<DirectoryProviderBody>))]
internal sealed partial class DirectoryExportJson : JsonSerializerContext;
