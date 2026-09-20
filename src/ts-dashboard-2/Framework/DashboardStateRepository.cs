using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard2;

#if TERRABASE_SDK
using TerraScale.DotNet;

/// <summary>Durable live dashboard aggregate storage. The document key is the
/// validated identity-provider subject; raw bearer tokens never enter storage.</summary>
public sealed class DashboardStateRepository(DashboardSecurityOptions security)
{
    private const string Collection = "dashboard_account_worlds";
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    // The token belongs to the request that loaded the world. A process-wide
    // subject dictionary lets concurrent requests overwrite one another's
    // expected token and can turn a conflict into a silent last-writer win.
    private readonly AsyncLocal<string?> _loadedChangeToken = new();
    private readonly TerraScaleClient _client = CreateClient(security.State!);

    public async Task<AccountWorld?> LoadAsync(string subject, CancellationToken ct)
    {
        try
        {
            var document = await _client.Documents.GetAsync(security.State!.Database, Collection, DocumentId(subject), ct);
            _loadedChangeToken.Value = document.ChangeToken;
            return document.Data.Deserialize<StateDocument>(Json)?.World;
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _loadedChangeToken.Value = null;
            return null;
        }
    }

    public async Task SaveAsync(string subject, string email, AccountWorld world, CancellationToken ct)
    {
        var body = new StateDocument(subject, email, world);
        var expected = _loadedChangeToken.Value;
        TerraScaleDocument result;
        try
        {
            result = await _client.Documents.PutAsync(
                security.State!.Database, Collection, DocumentId(subject), body,
                new TerraScaleDocumentMutationOptions(ExpectedChangeToken: expected, Durability: TerraScaleDurability.Global), ct);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            _loadedChangeToken.Value = null;
            throw new DashboardStateConflictException(subject);
        }
        _loadedChangeToken.Value = result.ChangeToken;
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
    public Task<AccountWorld?> LoadAsync(string subject, CancellationToken ct) => throw new InvalidOperationException("The live dashboard requires the TerraScale SDK state adapter.");
    public Task SaveAsync(string subject, string email, AccountWorld world, CancellationToken ct) => throw new InvalidOperationException("The live dashboard requires the TerraScale SDK state adapter.");
}
#endif
