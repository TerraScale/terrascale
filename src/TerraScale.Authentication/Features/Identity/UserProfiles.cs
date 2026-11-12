using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Identity;

public sealed class UserProfiles(AuthData data, Users users, PasswordHasher passwords, WebhookOutbox outbox, TimeProvider clock)
{
    public async Task<UserRecord> CreateAsync(string project, CreateProjectUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = Users.NormalizeEmail(request.Email) ?? throw Invalid("email is invalid.");
        if (request.Password is { } password && PasswordPolicy.Check(password, email) is var problem && problem != PasswordProblem.None)
        {
            throw Invalid(PasswordPolicy.Message(problem));
        }

        var hash = request.Password is { } value ? await passwords.HashAsync(value, cancellationToken).ConfigureAwait(false) : null;
        return await this.InsertAsync(project, email, hash, false, request.FirstName, request.LastName, request.ImageUrl, request.ExternalId,
            request.PublicMetadata, request.PrivateMetadata, request.UnsafeMetadata, cancellationToken).ConfigureAwait(false);
    }

    public Task<UserRecord> ImportAsync(string project, ImportProjectUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = Users.NormalizeEmail(request.Email) ?? throw Invalid("email is invalid.");
        if (request.PasswordHash is { } hash && !ImportedPasswords.Valid(hash))
        {
            throw Invalid("password_hash must be bounded bcrypt, argon2id PHC or pbkdf2_sha256$iterations$base64-salt$base64-hash.");
        }

        return this.InsertAsync(project, email, request.PasswordHash, request.EmailVerified, request.FirstName, request.LastName, request.ImageUrl,
            request.ExternalId, request.PublicMetadata, request.PrivateMetadata, request.UnsafeMetadata, cancellationToken);
    }

    public async Task<UserRecord> UpdateAsync(string project, string id, UserProfilePatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var current = await users.GetAsync(project, id, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        return await this.SaveProfileAsync(current, patch, null, cancellationToken).ConfigureAwait(false);
    }

    public Task<UserRecord> UpdateSelfAsync(UserRecord current, SelfProfilePatch patch, Change session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return this.SaveProfileAsync(current, new UserProfilePatch { FirstName = patch.FirstName, LastName = patch.LastName, UnsafeMetadata = patch.UnsafeMetadata },
            session, cancellationToken);
    }

    private async Task<UserRecord> SaveProfileAsync(UserRecord current, UserProfilePatch patch, Change? authorization, CancellationToken cancellationToken)
    {
        var updated = current with
        {
            FirstName = Field(patch.FirstName, current.FirstName, 255),
            LastName = Field(patch.LastName, current.LastName, 255),
            ImageUrl = Image(Field(patch.ImageUrl, current.ImageUrl, 2048)),
            ExternalId = Field(patch.ExternalId, current.ExternalId, 255),
            PublicMetadata = Metadata(current.PublicMetadata, patch.PublicMetadata),
            PrivateMetadata = Metadata(current.PrivateMetadata, patch.PrivateMetadata),
            UnsafeMetadata = Metadata(current.UnsafeMetadata, patch.UnsafeMetadata),
        };
        List<Change> changes = [Change.Update(updated), Change.Insert(outbox.NewEvent(current.InstanceId, "user.updated", current.Id, updated.Email))];
        if (authorization is not null)
        {
            changes.Add(authorization);
        }

        if (!await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(409, "user_conflict", "User changed concurrently or external_id is already assigned.");
        }

        return updated;
    }

    public async Task<UserRecord> UpgradePasswordAsync(UserRecord current, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        var hash = await passwords.HashAsync(password, cancellationToken).ConfigureAwait(false);
        var updated = current with { PasswordHash = hash };
        if (!await data.SaveAsync(Change.Update(updated), cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(409, "user_conflict", "User changed during sign-in; sign in again.");
        }

        return updated;
    }

    private async Task<UserRecord> InsertAsync(string project, string email, string? hash, bool verified, string? first, string? last,
        string? image, string? external, JsonElement publicMetadata, JsonElement privateMetadata, JsonElement unsafeMetadata, CancellationToken cancellationToken)
    {
        var user = new UserRecord(AuthIds.New(), project, email, 0, clock.GetUtcNow(), hash)
        {
            AuthStamp = AuthIds.New(),
            EmailVerified = verified,
            FirstName = Text(first, 255),
            LastName = Text(last, 255),
            ImageUrl = Image(Text(image, 2048)),
            ExternalId = Text(external, 255),
            PublicMetadata = Metadata("{}", publicMetadata),
            PrivateMetadata = Metadata("{}", privateMetadata),
            UnsafeMetadata = Metadata("{}", unsafeMetadata),
        };
        if (!await data.SaveAsync([Change.Insert(user), Change.Insert(outbox.NewEvent(project, "user.created", user.Id, email))], cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(409, "user_conflict", "email or external_id is already assigned in this project.");
        }

        return Users.BindAuthenticationState(user);
    }

    private static string? Field(JsonElement value, string? current, int maximum) => value.ValueKind switch
    {
        JsonValueKind.Undefined => current,
        JsonValueKind.Null => null,
        JsonValueKind.String => Text(value.GetString(), maximum),
        _ => throw Invalid("Profile fields must be strings or null."),
    };

    private static string? Text(string? value, int maximum) =>
        value is null || value.Length <= maximum && !value.Any(char.IsControl) ? value : throw Invalid($"Profile field exceeds {maximum} characters or contains control characters.");

    private static string? Image(string? value) => value is null || Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) ? value : throw Invalid("image_url must be an absolute https URL.");

    private static string Metadata(string current, JsonElement patch)
    {
        if (patch.ValueKind == JsonValueKind.Undefined)
        {
            return current;
        }

        if (patch.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(patch.GetRawText()) > 8192)
        {
            throw Invalid("Metadata must be a JSON object of at most 8192 UTF-8 bytes.");
        }

        var merged = Merge(JsonNode.Parse(current)!.AsObject(), patch).ToJsonString();
        return Encoding.UTF8.GetByteCount(merged) <= 8192 ? merged : throw Invalid("Merged metadata exceeds 8192 UTF-8 bytes.");
    }

    private static JsonObject Merge(JsonObject target, JsonElement patch)
    {
        foreach (var property in patch.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                target.Remove(property.Name);
            }
            else
            {
                target[property.Name] = property.Value.ValueKind == JsonValueKind.Object
                    ? Merge(target[property.Name] as JsonObject ?? new JsonObject(), property.Value)
                    : JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return target;
    }

    private static ApiException Invalid(string message) => new(400, "invalid_request", message);
}
