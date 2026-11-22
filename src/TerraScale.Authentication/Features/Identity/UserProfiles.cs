using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Identity;

public readonly union StoreUserResult(UserRecord, Validation, Conflict);

public readonly union UpdateProfileResult(UserRecord, NotFound, Validation, Conflict);

public sealed class UserProfiles(AuthData data, Users users, PasswordHasher passwords, WebhookOutbox outbox, TimeProvider clock)
{
    public async Task<StoreUserResult> CreateAsync(string project, CreateProjectUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = Users.NormalizeEmail(request.Email);
        if (email is null)
        {
            return new Validation("email is invalid.");
        }

        if (request.Password is { } password && PasswordPolicy.Check(password, email) is var problem && problem != PasswordProblem.None)
        {
            return new Validation(PasswordPolicy.Message(problem));
        }

        var hash = request.Password is { } value ? await passwords.HashAsync(project, value, cancellationToken).ConfigureAwait(false) : null;
        return await this.InsertAsync(project, email, hash, false, request.FirstName, request.LastName, request.ImageUrl, request.ExternalId,
            request.PublicMetadata, request.PrivateMetadata, request.UnsafeMetadata, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoreUserResult> ImportAsync(string project, ImportProjectUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = Users.NormalizeEmail(request.Email);
        if (email is null)
        {
            return new Validation("email is invalid.");
        }

        if (request.PasswordHash is { } hash && !ImportedPasswords.Valid(hash))
        {
            return new Validation($"password_hash must be canonical bcrypt (cost 04–12), argon2id PHC (memory × passes at most {2L * Passwords.MemoryKib * Passwords.Passes} KiB), or Django pbkdf2_sha256$iterations$salt$base64-hash (10000–{ImportedPasswords.MaximumPbkdf2Iterations} iterations).");
        }

        return await this.InsertAsync(project, email, request.PasswordHash, request.EmailVerified, request.FirstName, request.LastName, request.ImageUrl,
            request.ExternalId, request.PublicMetadata, request.PrivateMetadata, request.UnsafeMetadata, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateProfileResult> UpdateAsync(string project, string id, UserProfilePatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (await users.GetAsync(project, id, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return new NotFound("No such resource.", "not_found");
        }

        return await this.SaveProfileAsync(current, patch, null, cancellationToken).ConfigureAwait(false);
    }

    public Task<UpdateProfileResult> UpdateSelfAsync(UserRecord current, SelfProfilePatch patch, Change session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return this.SaveProfileAsync(current, new UserProfilePatch { FirstName = patch.FirstName, LastName = patch.LastName, UnsafeMetadata = patch.UnsafeMetadata },
            session, cancellationToken);
    }

    private async Task<UpdateProfileResult> SaveProfileAsync(UserRecord current, UserProfilePatch patch, Change? authorization, CancellationToken cancellationToken)
    {
        var firstName = Field(patch.FirstName, current.FirstName, 255);
        if (firstName.Error is { } firstError)
        {
            return firstError;
        }

        var lastName = Field(patch.LastName, current.LastName, 255);
        if (lastName.Error is { } lastError)
        {
            return lastError;
        }

        var imageField = Field(patch.ImageUrl, current.ImageUrl, 2048);
        if (imageField.Error is { } imageFieldError)
        {
            return imageFieldError;
        }

        var imageUrl = Image(imageField.Value);
        if (imageUrl.Error is { } imageError)
        {
            return imageError;
        }

        var externalId = Field(patch.ExternalId, current.ExternalId, 255);
        if (externalId.Error is { } externalError)
        {
            return externalError;
        }

        var publicMetadata = Metadata(current.PublicMetadata, patch.PublicMetadata);
        if (publicMetadata.Error is { } publicError)
        {
            return publicError;
        }

        var privateMetadata = Metadata(current.PrivateMetadata, patch.PrivateMetadata);
        if (privateMetadata.Error is { } privateError)
        {
            return privateError;
        }

        var unsafeMetadata = Metadata(current.UnsafeMetadata, patch.UnsafeMetadata);
        if (unsafeMetadata.Error is { } unsafeError)
        {
            return unsafeError;
        }

        var updated = current with
        {
            FirstName = firstName.Value,
            LastName = lastName.Value,
            ImageUrl = imageUrl.Value,
            ExternalId = externalId.Value,
            PublicMetadata = publicMetadata.Value!,
            PrivateMetadata = privateMetadata.Value!,
            UnsafeMetadata = unsafeMetadata.Value!,
            CustomClaims = TrustedClaims.Replace(current.CustomClaims, patch.CustomClaims),
        };
        if (updated == current)
        {
            return current;
        }

        List<Change> changes = [Change.Update(updated), Change.Insert(outbox.NewEvent(current.InstanceId, "user.updated", current.Id, updated.Email))];
        if (authorization is not null)
        {
            changes.Add(authorization);
        }

        return !await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false)
            ? new Conflict("User changed concurrently or external_id is already assigned.", "user_conflict")
            : updated;
    }

    public async Task<UserRecord?> UpgradePasswordAsync(UserRecord current, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        var hash = await passwords.HashAsync(current.InstanceId, password, cancellationToken).ConfigureAwait(false);
        var updated = current with { PasswordHash = hash };
        if (!await data.SaveAsync(Change.Update(updated), cancellationToken).ConfigureAwait(false))
        {
            var latest = await users.GetAsync(current.InstanceId, current.Id, cancellationToken).ConfigureAwait(false);
            return latest is { Disabled: false } && latest.AuthStamp == current.AuthStamp && latest.AuthEpoch == current.AuthEpoch
                && await passwords.VerifyAsync(current.InstanceId, password, latest.PasswordHash, cancellationToken).ConfigureAwait(false)
                ? latest : null;
        }

        return updated;
    }

    private async Task<StoreUserResult> InsertAsync(string project, string email, string? hash, bool verified, string? first, string? last,
        string? image, string? external, JsonElement publicMetadata, JsonElement privateMetadata, JsonElement unsafeMetadata, CancellationToken cancellationToken)
    {
        var firstName = Text(first, 255);
        if (firstName.Error is { } firstError)
        {
            return firstError;
        }

        var lastName = Text(last, 255);
        if (lastName.Error is { } lastError)
        {
            return lastError;
        }

        var imageText = Text(image, 2048);
        if (imageText.Error is { } imageTextError)
        {
            return imageTextError;
        }

        var imageUrl = Image(imageText.Value);
        if (imageUrl.Error is { } imageError)
        {
            return imageError;
        }

        var externalId = Text(external, 255);
        if (externalId.Error is { } externalError)
        {
            return externalError;
        }

        var publicJson = Metadata("{}", publicMetadata);
        if (publicJson.Error is { } publicError)
        {
            return publicError;
        }

        var privateJson = Metadata("{}", privateMetadata);
        if (privateJson.Error is { } privateError)
        {
            return privateError;
        }

        var unsafeJson = Metadata("{}", unsafeMetadata);
        if (unsafeJson.Error is { } unsafeError)
        {
            return unsafeError;
        }

        var user = new UserRecord(AuthIds.New(), project, email, 0, clock.GetUtcNow(), hash)
        {
            AuthStamp = AuthIds.New(),
            EmailVerified = verified,
            FirstName = firstName.Value,
            LastName = lastName.Value,
            ImageUrl = imageUrl.Value,
            ExternalId = externalId.Value,
            PublicMetadata = publicJson.Value!,
            PrivateMetadata = privateJson.Value!,
            UnsafeMetadata = unsafeJson.Value!,
        };
        return !await data.SaveAsync([Change.Insert(user), Change.Insert(outbox.NewEvent(project, "user.created", user.Id, email))], cancellationToken).ConfigureAwait(false)
            ? new Conflict("email or external_id is already assigned in this project.", "user_conflict")
            : Users.BindAuthenticationState(user);
    }

    private static (Validation? Error, string? Value) Field(JsonElement value, string? current, int maximum) => value.ValueKind switch
    {
        JsonValueKind.Undefined => (null, current),
        JsonValueKind.Null => (null, null),
        JsonValueKind.String => Text(value.GetString(), maximum),
        _ => (new Validation("Profile fields must be strings or null."), null),
    };

    private static (Validation? Error, string? Value) Text(string? value, int maximum) =>
        value is null || value.Length <= maximum && !value.Any(character => char.IsControl(character) || character is '\u200e' or '\u200f' or '\u061c' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069')
            ? (null, value)
            : (new Validation($"Profile field exceeds {maximum} characters or contains control or bidirectional formatting characters."), null);

    private static (Validation? Error, string? Value) Image(string? value) => value is null || Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
            ? (null, value)
            : (new Validation("image_url must be an absolute https URL."), null);

    private static (Validation? Error, string? Value) Metadata(string current, JsonElement patch)
    {
        var target = JsonNode.Parse(current)!.AsObject();
        var merged = current;
        if (patch.ValueKind != JsonValueKind.Undefined)
        {
            if (patch.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(patch.GetRawText()) > 8192)
            {
                return (new Validation("Metadata must be a JSON object of at most 8192 UTF-8 bytes."), null);
            }

            var result = Merge(target.DeepClone().AsObject(), patch);
            merged = JsonNode.DeepEquals(target, result) ? current : result.ToJsonString();
        }

        if (Encoding.UTF8.GetByteCount(merged) > 8192)
        {
            return (new Validation("Merged metadata exceeds 8192 UTF-8 bytes."), null);
        }

        try
        {
            using var document = JsonDocument.Parse(merged, new JsonDocumentOptions { MaxDepth = 10 });
        }
        catch (JsonException)
        {
            return (new Validation("Merged metadata exceeds the maximum depth of 10."), null);
        }

        return (null, merged);
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
}
