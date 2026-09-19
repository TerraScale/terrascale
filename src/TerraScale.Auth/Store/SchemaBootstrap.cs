namespace TerraScale.Auth.Store;

using TerraScale.DotNet;
using TerraScale.DotNet.Generated;

/// <summary>
/// Idempotent collection schema registration: creates every collection ts-auth
/// uses, with unique indexes over the synthetic key fields. Runs at startup,
/// exactly as an end-user application would bootstrap its TerraBase database.
/// </summary>
public static class SchemaBootstrap
{
    /// <summary>Applies all ts-auth collection schemas. Safe to run repeatedly.</summary>
    public static async Task EnsureSchemaAsync(TerraScaleClient client, string database, CancellationToken ct = default)
    {
        foreach (var (collection, fields) in Schemas())
        {
            await client.Management.Schema.PutAsync(
                database,
                collection,
                new CollectionSchema(Fields: fields, Fts: null, HistoryRetentionDays: null, Indexes: null, MergePolicy: null, Name: collection, Renames: null, Ttl: null, Version: null),
                idempotencyKey: $"ts-auth-schema-{collection}-v1",
                cancellationToken: ct);
        }
    }

    private static List<(string Collection, List<FieldDescriptor> Fields)> Schemas()
    {
        FieldDescriptor Field(string name, FieldType type, bool required = false, bool unique = false, bool indexed = false) =>
            new(Fts: null, Indexed: indexed || unique, MergePolicy: null, Name: name, Required: required ? true : null, Type: type, Unique: unique ? true : null);

        return
        [
            ("instances", [Field("id", FieldType.String, required: true), Field("slug", FieldType.String, required: true, unique: true), Field("name", FieldType.String, required: true)]),
            ("users", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true, indexed: true), Field("emailKey", FieldType.String, unique: true), Field("usernameKey", FieldType.String, unique: true), Field("banned", FieldType.Bool), Field("deleted", FieldType.Bool)]),
            ("passkeys", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true), Field("userId", FieldType.String, required: true, indexed: true), Field("credentialKey", FieldType.String, required: true, unique: true)]),
            ("sessions", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true), Field("userId", FieldType.String, required: true, indexed: true), Field("tokenKey", FieldType.String, required: true, unique: true)]),
            ("oauth_clients", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true, indexed: true), Field("clientKey", FieldType.String, required: true, unique: true)]),
            ("oauth_codes", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true), Field("codeKey", FieldType.String, required: true, unique: true), Field("userId", FieldType.String, required: true, indexed: true)]),
            ("refresh_tokens", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true), Field("tokenKey", FieldType.String, required: true, unique: true), Field("userId", FieldType.String, required: true, indexed: true), Field("clientId", FieldType.String, required: true, indexed: true)]),
            ("api_keys", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true, indexed: true), Field("secretKey", FieldType.String, required: true, unique: true)]),
            ("mfa_factors", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true), Field("userId", FieldType.String, required: true, indexed: true), Field("type", FieldType.String, required: true)]),
            ("organizations", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true, indexed: true), Field("slugKey", FieldType.String, required: true, unique: true)]),
            ("memberships", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true), Field("memberKey", FieldType.String, required: true, unique: true), Field("orgId", FieldType.String, required: true, indexed: true), Field("userId", FieldType.String, required: true, indexed: true)]),
            ("invitations", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true), Field("orgId", FieldType.String, required: true, indexed: true)]),
            ("verification_tokens", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true), Field("tokenKey", FieldType.String, required: true, unique: true), Field("purpose", FieldType.String, required: true)]),
            ("webhooks", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true, indexed: true)]),
            ("audit_events", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true, indexed: true)]),
            ("signing_keys", [Field("id", FieldType.String, required: true), Field("instanceId", FieldType.String, required: true, indexed: true), Field("active", FieldType.Bool)]),
        ];
    }
}
