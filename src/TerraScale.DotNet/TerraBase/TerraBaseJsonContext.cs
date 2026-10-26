using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// Source-generated JSON metadata for the TerraBase clients (trimming and native AOT safe). Web defaults give
/// camelCase names and read TerraBase's decimal-string numbers; snake_case Documents API members are pinned with
/// <see cref="JsonPropertyNameAttribute"/>. Null members are omitted: TerraBase rejects explicit nulls in operations.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = TerraBaseJsonContext.MaxResponseDepth,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(TerraBaseFileBucketRequest))]
[JsonSerializable(typeof(TerraBaseFileRulesRequest))]
[JsonSerializable(typeof(TerraBaseFileBucket))]
[JsonSerializable(typeof(TerraBaseStoredFile))]
[JsonSerializable(typeof(TerraBaseFileUrl))]
[JsonSerializable(typeof(TerraBaseFileUpload))]
[JsonSerializable(typeof(TerraBasePage<TerraBaseFileBucket>))]
[JsonSerializable(typeof(TerraBasePage<TerraBaseStoredFile>))]
[JsonSerializable(typeof(TerraBaseFunction))]
[JsonSerializable(typeof(TerraBaseFunction[]))]
[JsonSerializable(typeof(TerraBaseDeployFunctionRequest))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(TerraBaseDatabaseInfo))]
[JsonSerializable(typeof(TerraBaseCollection))]
[JsonSerializable(typeof(TerraBaseCollectionPage))]
[JsonSerializable(typeof(TerraBaseDocument))]
[JsonSerializable(typeof(TerraBaseDocumentPage))]
[JsonSerializable(typeof(TerraBaseQueryExplain))]
[JsonSerializable(typeof(TerraBasePlatformDatabaseList))]
[JsonSerializable(typeof(TerraBaseWriteResult))]
[JsonSerializable(typeof(TerraBaseQuery))]
[JsonSerializable(typeof(TerraBaseAggregate))]
[JsonSerializable(typeof(TerraBaseGroup))]
[JsonSerializable(typeof(TerraBasePatchOperations))]
[JsonSerializable(typeof(TerraBaseTransaction))]
[JsonSerializable(typeof(TerraBaseTransactionResult))]
[JsonSerializable(typeof(TerraBaseUniqueSchemaRequest))]
[JsonSerializable(typeof(TerraBaseUniqueSchema))]
[JsonSerializable(typeof(TerraBaseCollectionSchema))]
[JsonSerializable(typeof(TerraBaseOrganization))]
[JsonSerializable(typeof(TerraBasePage<TerraBaseOrganization>))]
[JsonSerializable(typeof(TerraBaseProject))]
[JsonSerializable(typeof(TerraBaseProjectClosure))]
[JsonSerializable(typeof(ClosureCancellationBody))]
[JsonSerializable(typeof(TerraBaseClosureCancellation))]
[JsonSerializable(typeof(TerraBaseHistoryErasure))]
[JsonSerializable(typeof(TerraBasePage<TerraBaseProject>))]
[JsonSerializable(typeof(TerraBaseManagedDatabase))]
[JsonSerializable(typeof(TerraBasePage<TerraBaseManagedDatabase>))]
[JsonSerializable(typeof(TerraBaseMember))]
[JsonSerializable(typeof(TerraBasePage<TerraBaseMember>))]
[JsonSerializable(typeof(TerraBaseCreatedInvitation))]
[JsonSerializable(typeof(TerraBasePage<TerraBaseInvitation>))]
[JsonSerializable(typeof(TerraBaseOwnershipTransfer))]
[JsonSerializable(typeof(TerraBaseAuditRetention))]
[JsonSerializable(typeof(TerraBasePage<TerraBaseAuditEvent>))]
[JsonSerializable(typeof(TerraBaseSqlResult))]
[JsonSerializable(typeof(TerraBaseSqlRequest))]
[JsonSerializable(typeof(TerraBaseSqlExecuteRequest))]
[JsonSerializable(typeof(TerraBaseSqlExecuteResponse))]
[JsonSerializable(typeof(TerraBaseCreateIndexRequest))]
[JsonSerializable(typeof(TerraBaseIndex))]
[JsonSerializable(typeof(TerraBaseIndexList))]
[JsonSerializable(typeof(TerraBaseDatabaseRegionsRequest))]
[JsonSerializable(typeof(TerraBaseDatabaseUsage))]
[JsonSerializable(typeof(TerraBaseProjectUsage))]
[JsonSerializable(typeof(TerraBaseCollectionList))]
[JsonSerializable(typeof(TerraBaseDeclaredSchema))]
[JsonSerializable(typeof(TerraBaseCollectionTtl))]
[JsonSerializable(typeof(TerraBaseChange))]
[JsonSerializable(typeof(TerraBaseChangeCursor))]
[JsonSerializable(typeof(TerraBaseStreamError))]
[JsonSerializable(typeof(TerraBaseExportLine))]
[JsonSerializable(typeof(TerraBaseImportJob))]
[JsonSerializable(typeof(TerraBaseSqlCheck))]
[JsonSerializable(typeof(TerraBaseQueryShapeList))]
[JsonSerializable(typeof(TerraBaseSavedQuery))]
[JsonSerializable(typeof(TerraBasePage<TerraBaseSavedQuery>))]
[JsonSerializable(typeof(TerraBaseSavedQueryRequest))]
[JsonSerializable(typeof(TerraBaseAutoIndexPolicy))]
[JsonSerializable(typeof(TerraBaseAutoIndexPolicyRequest))]
[JsonSerializable(typeof(TerraBaseIndexRecommendationList))]
[JsonSerializable(typeof(TerraBaseAppliedIndexRecommendation))]
[JsonSerializable(typeof(TerraBaseBackup))]
[JsonSerializable(typeof(TerraBaseCreateChangeWebhookRequest))]
[JsonSerializable(typeof(TerraBaseCreatedChangeWebhook))]
[JsonSerializable(typeof(IReadOnlyList<TerraBaseChangeWebhook>))]
[JsonSerializable(typeof(IReadOnlyList<TerraBaseChangeWebhookDelivery>))]
[JsonSerializable(typeof(TerraBasePage<TerraBaseBackup>))]
[JsonSerializable(typeof(TerraBaseBackupPolicy))]
[JsonSerializable(typeof(TerraBaseBackupPolicyRequest))]
[JsonSerializable(typeof(TerraBaseRestoreWindow))]
[JsonSerializable(typeof(TerraBasePointInTimePlan))]
[JsonSerializable(typeof(TerraBasePointInTimeRequest))]
[JsonSerializable(typeof(TerraBaseRestoredDatabase))]
[JsonSerializable(typeof(TerraBaseBackupDestination))]
[JsonSerializable(typeof(TerraBaseBackupDestinationRequest))]
[JsonSerializable(typeof(TerraBaseProjectAnalytics))]
[JsonSerializable(typeof(TerraBaseProjectReplication))]
[JsonSerializable(typeof(TerraBaseStatus))]
[JsonSerializable(typeof(TerraBaseReplicationHealth))]
[JsonSerializable(typeof(TerraBaseShapeManifest))]
[JsonSerializable(typeof(RequestBody))]
internal sealed partial class TerraBaseJsonContext : JsonSerializerContext
{
    // The producer reads retained documents up to 1000 levels, plus eight levels for response envelopes.
    internal const int MaxResponseDepth = 1008;

    /// <summary>
    /// The context every client uses. Request bodies are JSON API payloads, never embedded in HTML, so they keep
    /// <c>&lt;</c>, <c>&amp;</c> and non-ASCII text as written: the default encoder escapes them up to 6× larger and
    /// can push a document inside TerraBase's 1 MiB limit past it.
    /// </summary>
    public static TerraBaseJsonContext Wire { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = MaxResponseDepth,
    });

    internal static void RequireNonNullItems<T>(IReadOnlyList<T> items)
    {
        if (items is null || items.Any(item => item is null))
        {
            throw new JsonException("Response collections cannot contain null items.");
        }
    }
}

/// <summary>Reads and writes the Documents API's decimal-string Unix milliseconds.</summary>
internal sealed class UnixMillisecondsConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            && milliseconds >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds() && milliseconds <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : throw new JsonException("expected decimal Unix milliseconds");

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
}

/// <summary>Every small request body; members left null are omitted.</summary>
internal sealed record RequestBody
{
    public string? Name { get; init; }

    public TerraBasePlan? Plan { get; init; }

    public TerraBaseRole? Role { get; init; }

    public string? Email { get; init; }

    public string? Token { get; init; }

    public string? UserId { get; init; }

    public string? OrganizationId { get; init; }

    public int? Days { get; init; }

    public TerraBaseImportFormat? Format { get; init; }

    public long? SourceBytes { get; init; }

    public string? SourceSha256 { get; init; }

    public TerraBaseImportOptions? Options { get; init; }

    public string? PreviewId { get; init; }

    public int? Version { get; init; }

    public string? ClosureId { get; init; }

    public string? AuthorityOrganizationId { get; init; }

    public DateTimeOffset? RequestedAt { get; init; }

    public string? SettlementToken { get; init; }
}
