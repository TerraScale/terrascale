using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.AttackProtection;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Storage;

[module: DapperAot]
[module: TypeHandler(typeof(DateTimeOffset), typeof(InstantHandler))]
[module: TypeHandler(typeof(IReadOnlyList<string>), typeof(StringListHandler))]
[module: TypeHandler(typeof(IReadOnlyList<CredentialBinding>), typeof(CredentialBindingListHandler))]
[module: TypeHandler(typeof(IReadOnlyDictionary<string, JsonElement>), typeof(JsonPropertiesHandler))]
[module: TypeHandler(typeof(IReadOnlyDictionary<string, string>), typeof(StringDictionaryHandler))]
[module: TypeHandler(typeof(JsonElement), typeof(JsonElementHandler))]
[module: TypeHandler(typeof(EmailActor), typeof(EmailActorHandler))]
[module: TypeHandler(typeof(EmailBranding), typeof(EmailBrandingHandler))]
[module: TypeHandler(typeof(IReadOnlyList<EmailTemplateOverride>), typeof(EmailTemplateListHandler))]
[module: TypeHandler(typeof(OwnershipTransferRecord), typeof(OwnershipTransferHandler))]
[module: TypeHandler(typeof(AttackProtectionSettings), typeof(AttackProtectionHandler))]
[module: TypeHandler(typeof(ConfigurationBody), typeof(ConfigurationBodyHandler))]
[module: TypeHandler(typeof(IReadOnlyList<FooterLinkBody>), typeof(FooterLinkListHandler))]
[module: TypeHandler(typeof(IReadOnlyList<SocialProviderRecord>), typeof(SocialProviderListHandler))]
[module: TypeHandler(typeof(RestrictionsBody), typeof(RestrictionsBodyHandler))]

namespace TerraScale.Authentication.Storage;

public static class AuthDb
{
    private const string InstantFormat = "yyyy'-'MM'-'dd' 'HH':'mm':'ss'.'fffffff";

    public static string Instant(DateTimeOffset value) => value.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);

    public static string? Instant(DateTimeOffset? value) => value is { } instant ? Instant(instant) : null;

    public static DateTimeOffset ParseInstant(string text) =>
        DateTimeOffset.ParseExact(text, InstantFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static string? Json<T>(T? value, JsonTypeInfo<T> type) => value is null ? null : JsonSerializer.Serialize(value, type);
}

public sealed class InstantHandler : DbValueHandler<DateTimeOffset>
{
    protected override DateTimeOffset Parse(object? value) => value is string text
        ? AuthDb.ParseInstant(text)
        : throw new InvalidOperationException("Instants are stored as text.");

    protected override void SetValueCore(DbParameter parameter, DateTimeOffset value) => parameter.Value = AuthDb.Instant(value);
}

public abstract class JsonHandler<T>(JsonTypeInfo<T> type) : DbValueHandler<T>
{
    protected override T Parse(object? value) => value is string text && JsonSerializer.Deserialize(text, type) is { } parsed
        ? parsed
        : throw new InvalidOperationException($"A {typeof(T).Name} column holds no JSON value.");

    protected override void SetValueCore(DbParameter parameter, T value) => parameter.Value = JsonSerializer.Serialize(value, type);
}

public sealed class StringListHandler() : JsonHandler<IReadOnlyList<string>>(StorageJson.Default.IReadOnlyListString);

public sealed class CredentialBindingListHandler() : JsonHandler<IReadOnlyList<CredentialBinding>>(StorageJson.Default.IReadOnlyListCredentialBinding);

public sealed class JsonPropertiesHandler() : JsonHandler<IReadOnlyDictionary<string, JsonElement>>(StorageJson.Default.IReadOnlyDictionaryStringJsonElement);

public sealed class StringDictionaryHandler() : JsonHandler<IReadOnlyDictionary<string, string>>(StorageJson.Default.IReadOnlyDictionaryStringString);

public sealed class JsonElementHandler() : JsonHandler<JsonElement>(StorageJson.Default.JsonElement);

public sealed class EmailActorHandler() : JsonHandler<EmailActor>(EmailJson.Default.EmailActor);

public sealed class EmailBrandingHandler() : JsonHandler<EmailBranding>(EmailJson.Default.EmailBranding);

public sealed class EmailTemplateListHandler() : JsonHandler<IReadOnlyList<EmailTemplateOverride>>(EmailJson.Default.IReadOnlyListEmailTemplateOverride);

public sealed class OwnershipTransferHandler() : JsonHandler<OwnershipTransferRecord>(PlatformStorageJson.Default.OwnershipTransferRecord);

public sealed class AttackProtectionHandler() : JsonHandler<AttackProtectionSettings>(AttackProtectionJson.Default.AttackProtectionSettings);

public sealed class ConfigurationBodyHandler() : JsonHandler<ConfigurationBody>(ProjectAuthSettingsJson.Default.ConfigurationBody);

public sealed class FooterLinkListHandler() : JsonHandler<IReadOnlyList<FooterLinkBody>>(ProjectAuthSettingsJson.Default.IReadOnlyListFooterLinkBody);

public sealed class SocialProviderListHandler() : JsonHandler<IReadOnlyList<SocialProviderRecord>>(ProjectAuthSettingsJson.Default.IReadOnlyListSocialProviderRecord);

public sealed class RestrictionsBodyHandler() : JsonHandler<RestrictionsBody>(ProjectTenantJson.Default.RestrictionsBody);
