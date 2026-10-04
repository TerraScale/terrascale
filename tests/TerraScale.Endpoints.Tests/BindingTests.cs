using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Validation;

namespace TerraScale.Endpoints.Tests;

public sealed class BindingTests
{
    [Test]
    public async Task GetBindsRouteAndQueryIntoTheRequest()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await Assert.That(await client.GetStringAsync("/items/7?page=3&tags=a&tags=b&name=x"))
            .IsEqualTo("""{"id":7,"page":3,"name":"x","tags":["a","b"]}""");
        await Assert.That(await client.GetStringAsync("/items/7"))
            .IsEqualTo("""{"id":7,"page":1,"name":null,"tags":[]}""");
    }

    [Test]
    public async Task InvalidRouteOrQueryValuesAreRejected()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        using var response = await client.GetAsync("/items/7?page=abc");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(problem.RootElement.GetProperty("detail").GetString()).IsEqualTo("The value of 'page' is invalid.");
    }

    [Test]
    public async Task RouteValuesOverrideTheJsonBody()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        using var response = await client.PutAsync("/items/7", new StringContent("""{"id":1,"name":"new"}""", Encoding.UTF8, "application/json"));
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("""{"id":7,"page":1,"name":"new","tags":[]}""");
        using var bodyless = await client.PutAsync("/items/8", null);
        await Assert.That(await bodyless.Content.ReadAsStringAsync()).IsEqualTo("""{"id":8,"page":1,"name":null,"tags":[]}""");
    }

    [Test]
    public async Task ValidationRejectsInvalidRequests()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        using var invalid = await client.PostAsync("/validated", new StringContent("""{"name":"ab"}""", Encoding.UTF8, "application/json"));
        using var valid = await client.PostAsync("/validated", new StringContent("""{"name":"abc"}""", Encoding.UTF8, "application/json"));
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        await Assert.That(problem.RootElement.GetProperty("errors").TryGetProperty("Name", out _)).IsTrue();
        await Assert.That(valid.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    [Arguments("""{"name":"taken"}""", "Name", "'taken' is already taken.")]
    [Arguments("""{"name":"free","code":"bad"}""", "Code", "Code is rejected.")]
    public async Task AsyncValidationRejectsInvalidRequests(string body, string key, string message)
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        using var response = await client.PostAsync("/async-validated", new StringContent(body, Encoding.UTF8, "application/json"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(problem.RootElement.GetProperty("errors").GetProperty(key)[0].GetString()).IsEqualTo(message);
        using var valid = await client.PostAsync("/async-validated", new StringContent("""{"name":"free"}""", Encoding.UTF8, "application/json"));
        await Assert.That(valid.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task EndpointRulesMergeWithAttributeErrors()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        using var invalid = await client.PostAsync("/rule-validated", new StringContent("""{"name":"xy"}""", Encoding.UTF8, "application/json"));
        using var taken = await client.PostAsync("/rule-validated", new StringContent("""{"name":"taken"}""", Encoding.UTF8, "application/json"));
        using var valid = await client.PostAsync("/rule-validated", new StringContent("""{"name":"free"}""", Encoding.UTF8, "application/json"));
        using var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        var messages = problem.RootElement.GetProperty("errors").GetProperty("Name").EnumerateArray().Select(item => item.GetString()).ToArray();
        await Assert.That(messages.Length).IsEqualTo(2);
        await Assert.That(messages).Contains("Name is reserved.");
        await Assert.That(taken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(valid.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task OpenApiDescribesGetRequestsAsParameters()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var get = document.RootElement.GetProperty("paths").GetProperty("/items/{id}").GetProperty("get");
        var parameters = get.GetProperty("parameters").EnumerateArray()
            .Select(parameter => $"{parameter.GetProperty("in").GetString()}:{parameter.GetProperty("name").GetString()}")
            .ToArray();
        await Assert.That(parameters).Contains("path:id");
        await Assert.That(parameters).Contains("query:page");
        await Assert.That(parameters).Contains("query:tags");
        await Assert.That(get.TryGetProperty("requestBody", out _)).IsFalse();
        await Assert.That(get.GetProperty("tags")[0].GetString()).IsEqualTo("items");
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddValidation();
        builder.Services.AddTerraScaleEndpoints([
            EndpointRegistration.Create<GetItemEndpoint>(),
            EndpointRegistration.Create<PutItemEndpoint>(),
            EndpointRegistration.Create<ValidatedEndpoint>(),
            EndpointRegistration.Create<AsyncValidatedEndpoint>(),
            EndpointRegistration.Create<RuleValidatedEndpoint>()]);
        builder.Services.AddSingleton<TakenNames>();
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

public sealed record ItemQuery(int Id, int Page = 1, string[]? Tags = null, string? Name = null)
{
    public string[] Tags { get; init; } = Tags ?? [];
}

public sealed class GetItemEndpoint : Endpoint<ItemQuery, ItemQuery>
{
    public override void Configure() => Get("/items/{id:int}");

    public override ValueTask<ItemQuery> HandleAsync(ItemQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request);
}

public sealed class PutItemEndpoint : Endpoint<ItemQuery, ItemQuery>
{
    public override void Configure() => Put("/items/{id:int}");

    public override ValueTask<ItemQuery> HandleAsync(ItemQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request);
}

[ValidatableType]
public sealed class ValidatedRequest
{
    [Required, MinLength(3)]
    public string Name { get; set; } = "";
}

public sealed class ValidatedEndpoint : Endpoint<ValidatedRequest>
{
    public override void Configure() => Post("/validated");

    public override ValueTask HandleAsync(ValidatedRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class TakenNames
{
    public bool IsTaken(string name) => name == "taken";
}

/// <summary>.NET 11 async attribute: resolves a service and awaits, like a database lookup would.</summary>
public sealed class UniqueNameAttribute : AsyncValidationAttribute
{
    // Throwing proves the endpoint pipeline never takes the synchronous path.
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
        throw new NotSupportedException("Validate asynchronously.");

    protected override async Task<ValidationResult?> IsValidAsync(
        object? value,
        ValidationContext validationContext,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        return value is string name && validationContext.GetRequiredService<TakenNames>().IsTaken(name)
            ? new ValidationResult($"'{name}' is already taken.", [validationContext.MemberName!])
            : ValidationResult.Success;
    }
}

[ValidatableType]
public sealed class AsyncValidatedRequest : IAsyncValidatableObject
{
    [Required, UniqueName]
    public string Name { get; set; } = "";

    public string? Code { get; set; }

    public async IAsyncEnumerable<ValidationResult> ValidateAsync(
        ValidationContext validationContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (Code == "bad")
        {
            yield return new ValidationResult("Code is rejected.", [nameof(Code)]);
        }
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) => [];
}

public sealed class AsyncValidatedEndpoint : Endpoint<AsyncValidatedRequest>
{
    public override void Configure() => Post("/async-validated");

    public override ValueTask HandleAsync(AsyncValidatedRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Endpoint rule using a constructor-injected service, merged with the request's attribute errors.</summary>
public sealed class RuleValidatedEndpoint(TakenNames names) : Endpoint<ValidatedRequest>
{
    public override void Configure() => Post("/rule-validated");

    protected override async IAsyncEnumerable<ValidationResult> ValidateAsync(
        ValidatedRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (names.IsTaken(request.Name) || request.Name.StartsWith('x'))
        {
            yield return new ValidationResult("Name is reserved.", [nameof(ValidatedRequest.Name)]);
        }
    }

    public override ValueTask HandleAsync(ValidatedRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Regressions for review findings on PR #8.</summary>
public sealed class BindingEdgeTests
{
    [Test]
    public async Task CompactGuidsAndNumericCharsBindLikeMinimalApis()
    {
        await using var app = await StartAsync(mapInGroup: false);
        using var client = Client(app);
        await Assert.That(await client.GetStringAsync("/guid-items/6f9619ff8b86d011b42d00c04fc964ff?initial=7"))
            .IsEqualTo("""{"id":"6f9619ff-8b86-d011-b42d-00c04fc964ff","initial":"7"}""");
    }

    [Test]
    public async Task MergedBodiesHonorTheCharset()
    {
        await using var app = await StartAsync(mapInGroup: false);
        using var client = Client(app);
        using var content = new StringContent("""{"name":"utf16"}""", Encoding.Unicode, "application/json");
        using var response = await client.PutAsync("/items/7?page=2", content);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("""{"id":7,"page":2,"name":"utf16","tags":[]}""");
    }

    [Test]
    [Arguments("""{"name":"a","name":"b"}""")]
    [Arguments("""{"name":"a","Name":"b"}""")]
    public async Task DuplicateBodyPropertiesAreRejectedWhenMerging(string body)
    {
        await using var app = await StartAsync(mapInGroup: false);
        using var client = Client(app);
        using var response = await client.PutAsync("/items/7", new StringContent(body, Encoding.UTF8, "application/json"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(problem.RootElement.GetProperty("detail").GetString()).IsEqualTo("The JSON request body contains duplicate properties.");
    }

    [Test]
    public async Task SingleUseBindingSourcesAreConsumedOnce()
    {
        await using var app = await StartAsync(mapInGroup: false);
        using var client = Client(app);
        using var valid = await client.PostAsync("/once", null);
        await Assert.That(valid.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task GroupPrefixRouteValuesBind()
    {
        await using var app = await StartAsync(mapInGroup: true);
        using var client = Client(app);
        await Assert.That(await client.GetStringAsync("/tenants/acme/items?page=2")).IsEqualTo("""{"tenantId":"acme","page":2}""");
    }

    [Test]
    public async Task OpenApiUsesTheFinalRouteAndEachMethod()
    {
        await using var app = await StartAsync(mapInGroup: true);
        using var client = Client(app);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");

        var items = paths.GetProperty("/tenants/{tenantId}/items").GetProperty("get");
        await Assert.That(Parameters(items)).IsEquivalentTo(["path:tenantId", "query:page"]);
        await Assert.That(items.GetProperty("responses").TryGetProperty("404", out _)).IsTrue();

        var mixed = paths.GetProperty("/tenants/{tenantId}/mixed");
        await Assert.That(Parameters(mixed.GetProperty("get"))).IsEquivalentTo(["path:tenantId", "query:page"]);
        await Assert.That(mixed.GetProperty("get").TryGetProperty("requestBody", out _)).IsFalse();
        await Assert.That(Parameters(mixed.GetProperty("post"))).IsEquivalentTo(["path:tenantId"]);
        await Assert.That(mixed.GetProperty("post").TryGetProperty("requestBody", out _)).IsTrue();

        var optional = paths.GetProperty("/tenants/{tenantId}/optional/{id}").GetProperty("get").GetProperty("parameters");
        await Assert.That(optional.EnumerateArray().All(parameter => parameter.GetProperty("in").GetString() != "path"
            || parameter.GetProperty("required").GetBoolean())).IsTrue();
    }

    private static string[] Parameters(JsonElement operation) =>
        operation.TryGetProperty("parameters", out var parameters)
            ? [.. parameters.EnumerateArray().Select(item => $"{item.GetProperty("in").GetString()}:{item.GetProperty("name").GetString()}")]
            : [];

    private static async Task<WebApplication> StartAsync(bool mapInGroup)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(mapInGroup
            ? [
                EndpointRegistration.Create<TenantItemsEndpoint>(),
                EndpointRegistration.Create<MixedItemsEndpoint>(),
                EndpointRegistration.Create<OptionalItemEndpoint>()]
            : [
                EndpointRegistration.Create<GuidItemEndpoint>(),
                EndpointRegistration.Create<PutItemEndpoint>(),
                EndpointRegistration.Create<OnceBoundEndpoint>()]);
        var app = builder.Build();
        if (mapInGroup)
        {
            app.MapGroup("/tenants/{tenantId}").MapTerraScaleEndpoints();
        }
        else
        {
            app.MapTerraScaleEndpoints();
        }

        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

public sealed record GuidQuery(Guid Id, char Initial = 'a');

public sealed class GuidItemEndpoint : Endpoint<GuidQuery, GuidQuery>
{
    public override void Configure() => Get("/guid-items/{id:guid}");

    public override ValueTask<GuidQuery> HandleAsync(GuidQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

public sealed record TenantQuery(string TenantId, int Page = 1);

public sealed class TenantItemsEndpoint : Endpoint<TenantQuery, TenantQuery>
{
    public override void Configure()
    {
        Get("/items");
        Produces(StatusCodes.Status404NotFound);
    }

    public override ValueTask<TenantQuery> HandleAsync(TenantQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

public sealed class MixedItemsEndpoint : Endpoint<TenantQuery, TenantQuery>
{
    public override void Configure()
    {
        Verbs("GET", "POST");
        Route("/mixed");
    }

    public override ValueTask<TenantQuery> HandleAsync(TenantQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

public sealed record OptionalQuery(int? Id);

public sealed class OptionalItemEndpoint : Endpoint<OptionalQuery, OptionalQuery>
{
    public override void Configure() => Get("/optional/{id?}");

    public override ValueTask<OptionalQuery> HandleAsync(OptionalQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

/// <summary>Binds through a single-use IValueTaskSource and has endpoint rules, the path that consumed the binding twice.</summary>
public sealed class OnceBoundEndpoint : Endpoint<string>
{
    public override void Configure() => Post("/once");

    protected override ValueTask<string> BindAsync(CancellationToken cancellationToken) => new(new SingleUseSource("bound"), 0);

    protected override async IAsyncEnumerable<ValidationResult> ValidateAsync(
        string request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield break;
    }

    public override ValueTask HandleAsync(string request, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private sealed class SingleUseSource(string value) : System.Threading.Tasks.Sources.IValueTaskSource<string>
    {
        private bool consumed;

        public string GetResult(short token)
        {
            if (this.consumed)
            {
                throw new InvalidOperationException("The binding was consumed twice.");
            }

            this.consumed = true;
            return value;
        }

        public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token) =>
            System.Threading.Tasks.Sources.ValueTaskSourceStatus.Succeeded;

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags) => continuation(state);
    }
}

/// <summary>Regressions for the second ts-reviewer pass on PR #8: converters, body shapes and custom binders.</summary>
public sealed class ConverterBindingTests
{
    [Test]
    public async Task OpenApiKeepsBodiesOnlyWhereTheBinderNeedsThem()
    {
        await using var app = await StartAsync(builder => { });
        using var client = Client(app);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");

        // An object request with no properties binds without a body.
        await Assert.That(paths.GetProperty("/empty").GetProperty("get").TryGetProperty("requestBody", out _)).IsFalse();

        // Nested objects cannot come from the query string, so they keep the body.
        var filtered = paths.GetProperty("/filtered").GetProperty("get");
        await Assert.That(Parameters(filtered)).IsEquivalentTo(["query:page"]);
        await Assert.That(filtered.TryGetProperty("requestBody", out _)).IsTrue();

        // A BindAsync override owns binding: no query parameters and a required body.
        await Assert.That(Parameters(paths.GetProperty("/custom-bind").GetProperty("get"))).IsEmpty();
        await Assert.That(paths.GetProperty("/custom-bind").GetProperty("post").GetProperty("requestBody").GetProperty("required").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task PropertyLevelStringEnumConvertersBindAndDescribeNames()
    {
        await using var app = await StartAsync(builder => { });
        using var client = Client(app);
        await Assert.That(await client.GetStringAsync("/colors?color=Green")).IsEqualTo("""{"color":"Green"}""");

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var parameter = document.RootElement.GetProperty("paths").GetProperty("/colors").GetProperty("get").GetProperty("parameters")[0];
        await Assert.That(parameter.GetProperty("schema").GetRawText()).Contains("Green");
    }

    [Test]
    public async Task ApplicationConvertersAndNewNumberTypesBindUnderStrictNumbers()
    {
        await using var app = await StartAsync(builder => builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
            options.SerializerOptions.Converters.Add(new StringOnlyIntConverter());
        }));
        using var client = Client(app);
        await Assert.That(await client.GetStringAsync("/converted/123?page=3&ratio=1.5"))
            .IsEqualTo("""{"page":3,"count":123,"ratio":1.5}""");
    }

    [Test]
    public async Task PropertyConvertersBindWithoutStandaloneTypeMetadata()
    {
        await using var app = await StartAsync(
            builder => builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.TypeInfoResolver = MoneyJsonContext.Default),
            EndpointRegistration.Create<MoneyEndpoint>());
        using var client = Client(app);
        await Assert.That(await client.GetStringAsync("/money?amount=12.5")).IsEqualTo("12.5");
    }

    private static string[] Parameters(JsonElement operation) =>
        operation.TryGetProperty("parameters", out var parameters)
            ? [.. parameters.EnumerateArray().Select(item => $"{item.GetProperty("in").GetString()}:{item.GetProperty("name").GetString()}")]
            : [];

    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints.Length > 0
            ? endpoints
            : [
                EndpointRegistration.Create<EmptyRequestEndpoint>(),
                EndpointRegistration.Create<FilteredEndpoint>(),
                EndpointRegistration.Create<CustomBindEndpoint>(),
                EndpointRegistration.Create<ColorEndpoint>(),
                EndpointRegistration.Create<ConvertedEndpoint>()]);
        configure(builder);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

public sealed record EmptyRequest;

public sealed class EmptyRequestEndpoint : Endpoint<EmptyRequest, string>
{
    public override void Configure() => Get("/empty");

    public override ValueTask<string> HandleAsync(EmptyRequest request, CancellationToken cancellationToken) => ValueTask.FromResult("empty");
}

public sealed record NestedFilter(string Name);

public sealed record FilteredQuery(int Page = 1, NestedFilter? Filter = null);

public sealed class FilteredEndpoint : Endpoint<FilteredQuery, string>
{
    public override void Configure() => Get("/filtered");

    public override ValueTask<string> HandleAsync(FilteredQuery request, CancellationToken cancellationToken) => ValueTask.FromResult("filtered");
}

public sealed class CustomBindEndpoint : Endpoint<ItemQuery, string>
{
    public override void Configure()
    {
        Verbs("GET", "POST");
        Route("/custom-bind");
    }

    protected override ValueTask<ItemQuery> BindAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new ItemQuery(1));

    public override ValueTask<string> HandleAsync(ItemQuery request, CancellationToken cancellationToken) => ValueTask.FromResult("custom");
}

public enum Color
{
    Red,
    Green,
}

public sealed record ColorQuery([property: JsonConverter(typeof(JsonStringEnumConverter<Color>))] Color Color);

public sealed class ColorEndpoint : Endpoint<ColorQuery, ColorQuery>
{
    public override void Configure() => Get("/colors");

    public override ValueTask<ColorQuery> HandleAsync(ColorQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

/// <summary>An application converter that only reads string tokens, like a legacy wire format.</summary>
public sealed class StringOnlyIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? int.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            : throw new JsonException("Expected a string.");

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

public sealed record ConvertedQuery(int Page, Int128 Count, Half Ratio);

public sealed class ConvertedEndpoint : Endpoint<ConvertedQuery, ConvertedQuery>
{
    public override void Configure() => Get("/converted/{count}");

    public override ValueTask<ConvertedQuery> HandleAsync(ConvertedQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

public sealed class Money(decimal value)
{
    public decimal Value { get; } = value;
}

public sealed class MoneyConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(decimal.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture));

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed record MoneyQuery([property: JsonConverter(typeof(MoneyConverter))] Money Amount);

public sealed class MoneyEndpoint : Endpoint<MoneyQuery, string>
{
    public override void Configure() => Get("/money");

    public override ValueTask<string> HandleAsync(MoneyQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.Amount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>Source-generated only, so there is no reflection fallback for types it does not list.</summary>
[JsonSerializable(typeof(MoneyQuery))]
internal sealed partial class MoneyJsonContext : JsonSerializerContext;
