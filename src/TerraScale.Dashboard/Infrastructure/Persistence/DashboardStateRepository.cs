using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Infrastructure.Persistence;

#if TERRABASE_SDK
using TerraScale.DotNet;

/// <summary>Durable live dashboard aggregate storage. The document key is the
/// validated identity-provider subject; raw bearer tokens never enter storage.</summary>
public sealed class DashboardStateRepository(DashboardSecurityOptions security)
{
    private const string Collection = "dashboard_account_worlds";
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private readonly TerraScaleClient _client = CreateClient(security.State!);

    /// <summary>Loads the stored world with its change token. The token is the
    /// caller's concurrency ticket: pass it back to <see cref="SaveAsync"/>,
    /// which throws <see cref="DashboardStateConflictException"/> when another
    /// writer committed first.</summary>
    public async Task<(AccountWorld? World, string? ChangeToken)> LoadAsync(string subject, CancellationToken ct)
    {
        try
        {
            var document = await _client.Documents.GetAsync(security.State!.Database, Collection, DocumentId(subject), ct);
            return (document.Data.Deserialize<StateDocument>(Json)?.World, document.ChangeToken);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    public async Task<string> SaveAsync(string subject, string email, AccountWorld world, string? expectedChangeToken, CancellationToken ct)
    {
        // Serialize with this repository's options (default ImmutableArray →
        // empty) before handing plain JSON to the SDK: the SDK's own
        // serialization cannot know about default-array worlds.
        var body = JsonSerializer.SerializeToElement(new StateDocument(subject, email, world), Json);
        try
        {
            var result = await _client.Documents.PutAsync(
                security.State!.Database, Collection, DocumentId(subject), body,
                new TerraScaleDocumentMutationOptions(ExpectedChangeToken: expectedChangeToken, Durability: TerraScaleDurability.Global), ct);
            return result.ChangeToken!;
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            throw new DashboardStateConflictException(subject);
        }
    }

    private static TerraScaleClient CreateClient(DashboardStateOptions options) => new(
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = options.ApiUrl, Timeout = TimeSpan.FromSeconds(10) },
        new TerraScaleClientOptions { BaseAddress = options.ApiUrl, ApiKey = options.ApiKey, MaxRetryAttempts = 2 });

    private static string DocumentId(string subject) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(subject))).ToLowerInvariant();
    private sealed record StateDocument(string Subject, string Email, AccountWorld World);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { IncludeFields = true };
        options.Converters.Add(new ImmutableArrayConverterFactory());
        return options;
    }

    private sealed class ImmutableArrayConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>);

        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ImmutableArrayConverter<>).MakeGenericType(type.GetGenericArguments()[0]), options)!;
    }

    private sealed class ImmutableArrayConverter<T>(JsonSerializerOptions options) : JsonConverter<ImmutableArray<T>>
    {
        public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions _) =>
            JsonSerializer.Deserialize<List<T>>(ref reader, options)?.ToImmutableArray() ?? [];

        public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions _) =>
            JsonSerializer.Serialize(writer, value.IsDefault ? Array.Empty<T>() : value.ToArray(), options);
    }
}
#else
public sealed class DashboardStateRepository
{
    public Task<(AccountWorld? World, string? ChangeToken)> LoadAsync(string subject, CancellationToken ct) => throw new InvalidOperationException("The live dashboard requires the TerraScale SDK state adapter.");
    public Task<string> SaveAsync(string subject, string email, AccountWorld world, string? expectedChangeToken, CancellationToken ct) => throw new InvalidOperationException("The live dashboard requires the TerraScale SDK state adapter.");
}
#endif
