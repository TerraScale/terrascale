using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Validation;

namespace TerraScale.Endpoints.Tests;

// shortcut: net10.0 ValidationsGenerator (Microsoft.AspNetCore.App 10.0.12) fails with a duplicate ValidatableInfoResolver.g.cs when AddValidation() has two call sites, so every test enables validation here. Upgrade: inline AddValidation() again when the generator accepts it.
internal static class ValidationSetup
{
    internal static void Enable(WebApplicationBuilder builder) => builder.Services.AddValidation();
}

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
    public async Task SynchronousAttributesResolveServicesFromTheRequestScope()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        using var taken = await client.PostAsync("/reserved-code", new StringContent("""{"code":"taken"}""", Encoding.UTF8, "application/json"));
        using var free = await client.PostAsync("/reserved-code", new StringContent("""{"code":"free"}""", Encoding.UTF8, "application/json"));
        await Assert.That(taken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await taken.Content.ReadAsStringAsync());
        await Assert.That(problem.RootElement.GetProperty("errors").GetProperty("Code")[0].GetString()).IsEqualTo("Code 'taken' is already in use.");
        await Assert.That(free.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

#if NET11_0_OR_GREATER
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
#endif

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
        ValidationSetup.Enable(builder);
        builder.Services.AddTerraScaleEndpoints([
            EndpointRegistration.Create<GetItemEndpoint>(),
            EndpointRegistration.Create<PutItemEndpoint>(),
            EndpointRegistration.Create<ValidatedEndpoint>(),
            EndpointRegistration.Create<ReservedCodeEndpoint>(),
#if NET11_0_OR_GREATER
            EndpointRegistration.Create<AsyncValidatedEndpoint>(),
#endif
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

/// <summary>Synchronous attribute that resolves a service from the request scope, as a lookup would.</summary>
public sealed class ReservedCodeAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
        value is string code && validationContext.GetRequiredService<TakenNames>().IsTaken(code)
            ? new ValidationResult($"Code '{code}' is already in use.", [validationContext.MemberName!])
            : ValidationResult.Success;
}

[ValidatableType]
public sealed class ReservedCodeRequest
{
    [ReservedCode]
    public string Code { get; set; } = "";
}

public sealed class ReservedCodeEndpoint : Endpoint<ReservedCodeRequest>
{
    public override void Configure() => Post("/reserved-code");

    public override ValueTask HandleAsync(ReservedCodeRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

#if NET11_0_OR_GREATER
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
#endif

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
        // POST keeps the body and documents query values as optional, since the binder merges them too.
        await Assert.That(Parameters(mixed.GetProperty("post"))).IsEquivalentTo(["path:tenantId", "query:page"]);
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

/// <summary>Regressions from review of PR #8 (second pass): converters, body shapes and custom binders.</summary>
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

/// <summary>Regressions from review of PR #8 (third pass).</summary>
public sealed class ThirdPassBindingTests
{
    [Test]
    public async Task CollectionRouteValuesUseTheSimpleStyle()
    {
        await using var app = await StartAsync(_ => { });
        using var client = Client(app);
        await Assert.That(await client.GetStringAsync("/ids/1,2,3")).IsEqualTo("""{"ids":[1,2,3]}""");
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints.Length > 0
            ? endpoints
            : [
                EndpointRegistration.Create<RequiredFilterEndpoint>(),
                EndpointRegistration.Create<NestedEchoEndpoint>(),
                EndpointRegistration.Create<NullableConvertedEndpoint>(),
                EndpointRegistration.Create<IdsEndpoint>()]);
        configure(builder);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

public sealed class RequiredFilterQuery
{
    public required string Name { get; init; }

    public NestedFilter? Filter { get; init; }
}

public sealed class RequiredFilterEndpoint : Endpoint<RequiredFilterQuery, string>
{
    public override void Configure() => Get("/required-filter");

    public override ValueTask<string> HandleAsync(RequiredFilterQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request.Name);
}

public sealed class NestedEchoEndpoint : Endpoint<FilteredQuery, FilteredQuery>
{
    public override void Configure() => Post("/nested-echo");

    public override ValueTask<FilteredQuery> HandleAsync(FilteredQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

public sealed record NullableConvertedQuery(int? Limit, List<int?>? Extra);

public sealed class NullableConvertedEndpoint : Endpoint<NullableConvertedQuery, NullableConvertedQuery>
{
    public override void Configure() => Get("/nullable-converted");

    public override ValueTask<NullableConvertedQuery> HandleAsync(NullableConvertedQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request);
}

public sealed record IdsQuery(int[] Ids);

public sealed class IdsEndpoint : Endpoint<IdsQuery, IdsQuery>
{
    public override void Configure() => Get("/ids/{ids}");

    public override ValueTask<IdsQuery> HandleAsync(IdsQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

public sealed record CaseQuery([property: JsonPropertyName("id")] int Lower, [property: JsonPropertyName("ID")] int Upper);

public sealed class CaseEndpoint : Endpoint<CaseQuery, CaseQuery>
{
    public override void Configure() => Get("/case");

    public override ValueTask<CaseQuery> HandleAsync(CaseQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

/// <summary>Regressions from review of PR #8 (fourth pass).</summary>
public sealed class FourthPassBindingTests
{
    [Test]
    public async Task BodyMethodsDocumentOptionalQueryParameters()
    {
        await using var app = await StartAsync(_ => { });
        using var client = Client(app);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var post = document.RootElement.GetProperty("paths").GetProperty("/fourth/nested-echo").GetProperty("post");
        var page = post.GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "page");
        await Assert.That(page.GetProperty("in").GetString()).IsEqualTo("query");
        await Assert.That(page.TryGetProperty("required", out var required) && required.GetBoolean()).IsFalse();
        await Assert.That(post.TryGetProperty("requestBody", out _)).IsTrue();
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints.Length > 0
            ? endpoints
            : [
                EndpointRegistration.Create<FourthNestedEchoEndpoint>(),
                EndpointRegistration.Create<ByFilterEndpoint>(),
                EndpointRegistration.Create<StrictNumbersEndpoint>()]);
        configure(builder);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

public sealed class FourthNestedEchoEndpoint : Endpoint<FilteredQuery, FilteredQuery>
{
    public override void Configure() => Post("/fourth/nested-echo");

    public override ValueTask<FilteredQuery> HandleAsync(FilteredQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

public sealed class ByFilterEndpoint : Endpoint<FilteredQuery, string>
{
    public override void Configure() => Get("/fourth/by-filter/{filter}");

    public override ValueTask<string> HandleAsync(FilteredQuery request, CancellationToken cancellationToken) => ValueTask.FromResult("by-filter");
}

public sealed record StrictNumbersQuery(Int128 Big, int Page, long[] Ids, decimal Price, double Ratio);

public sealed class StrictNumbersEndpoint : Endpoint<StrictNumbersQuery, StrictNumbersQuery>
{
    public override void Configure() => Get("/fourth/strict/{big}");

    public override ValueTask<StrictNumbersQuery> HandleAsync(StrictNumbersQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request);
}

/// <summary>Regressions from review of PR #8 (fifth pass).</summary>
public sealed class FifthPassBindingTests
{
    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints([
            EndpointRegistration.Create<RequiredItemEndpoint>(),
            EndpointRegistration.Create<GetterOnlyEndpoint>(),
            EndpointRegistration.Create<NullableStructEndpoint>()]);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

public sealed class RequiredItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }
}

public sealed class RequiredItemEndpoint : Endpoint<RequiredItem, RequiredItem>
{
    public override void Configure() => Post("/fifth/items/{id}");

    public override ValueTask<RequiredItem> HandleAsync(RequiredItem request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

public sealed class GetterOnlyQuery
{
    public int Page { get; init; } = 1;

    public int Id => 42;

    [JsonIgnore]
    public int Hidden { get; set; }
}

public sealed class GetterOnlyEndpoint : Endpoint<GetterOnlyQuery, string>
{
    public override void Configure() => Get("/fifth/getter");

    public override ValueTask<string> HandleAsync(GetterOnlyQuery request, CancellationToken cancellationToken) => ValueTask.FromResult("getter");
}

public readonly record struct FilterStruct(string Name);

public sealed record NullableStructQuery(int Page = 1, FilterStruct? Filter = null);

public sealed class NullableStructEndpoint : Endpoint<NullableStructQuery, NullableStructQuery>
{
    public override void Configure() => Post("/fifth/struct-echo");

    public override ValueTask<NullableStructQuery> HandleAsync(NullableStructQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request);
}

/// <summary>Regressions from review of PR #8 (sixth pass).</summary>
public sealed class SixthPassBindingTests
{
    [Test]
    public async Task EnumNamesBindWithTheDefaultNumericConverter()
    {
        await using var app = await StartAsync(_ => { });
        using var client = Client(app);
        await Assert.That(await client.GetStringAsync("/sixth/enum?color=green&access=Read,Write")).IsEqualTo("""{"color":1,"access":3}""");
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints.Length > 0
            ? endpoints
            : [
                EndpointRegistration.Create<NullableValuesEndpoint>(),
                EndpointRegistration.Create<EnumNamesEndpoint>(),
                EndpointRegistration.Create<SixthEchoEndpoint>()]);
        configure(builder);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

public sealed record NullableValuesQuery(int? Limit, Color? Shade);

public sealed class NullableValuesEndpoint : Endpoint<NullableValuesQuery, NullableValuesQuery>
{
    public override void Configure() => Get("/sixth/nullable");

    public override ValueTask<NullableValuesQuery> HandleAsync(NullableValuesQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request);
}

[Flags]
public enum Access
{
    None = 0,
    Read = 1,
    Write = 2,
}

public sealed record EnumNamesQuery(Color Color, Access Access);

public sealed class EnumNamesEndpoint : Endpoint<EnumNamesQuery, EnumNamesQuery>
{
    public override void Configure() => Get("/sixth/enum");

    public override ValueTask<EnumNamesQuery> HandleAsync(EnumNamesQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

public sealed class SixthEchoEndpoint : Endpoint<FilteredQuery, FilteredQuery>
{
    public override void Configure() => Post("/sixth/echo");

    public override ValueTask<FilteredQuery> HandleAsync(FilteredQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

/// <summary>Regressions from review of PR #8 (seventh pass).</summary>
public sealed class SeventhPassBindingTests
{
    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints);
        configure(builder);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

/// <summary>Names only: numbers are rejected, so the binder must keep the string token.</summary>
public sealed class StrictColorConverter() : JsonStringEnumConverter<Color>(namingPolicy: null, allowIntegerValues: false);

public sealed record StrictColorQuery([property: JsonConverter(typeof(StrictColorConverter))] Color Color);

public sealed class StrictColorEndpoint : Endpoint<StrictColorQuery, StrictColorQuery>
{
    public override void Configure() => Get("/seventh/strict-color");

    public override ValueTask<StrictColorQuery> HandleAsync(StrictColorQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

public sealed record ColorsQuery(Color? Shade, List<Color> Colors);

public sealed class ColorsEndpoint : Endpoint<ColorsQuery, ColorsQuery>
{
    public override void Configure() => Get("/seventh/colors");

    public override ValueTask<ColorsQuery> HandleAsync(ColorsQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

/// <summary>Regressions from review of PR #8 (eighth pass).</summary>
public sealed class EighthPassBindingTests
{
    [Test]
    public async Task RequiredConstructorParametersAreRequiredQueryParameters()
    {
        await using var app = await StartAsync(
            builder => builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.RespectRequiredConstructorParameters = true),
            EndpointRegistration.Create<RequiredCtorEndpoint>());
        using var client = Client(app);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var parameters = document.RootElement.GetProperty("paths").GetProperty("/eighth/required-ctor").GetProperty("get").GetProperty("parameters");
        var page = parameters.EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "page");
        var size = parameters.EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "size");
        await Assert.That(page.GetProperty("required").GetBoolean()).IsTrue();
        await Assert.That(size.TryGetProperty("required", out var optional) && optional.GetBoolean()).IsFalse();
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints);
        configure(builder);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

public sealed record RequiredCtorQuery(int Page, int Size = 10);

public sealed class RequiredCtorEndpoint : Endpoint<RequiredCtorQuery, RequiredCtorQuery>
{
    public override void Configure() => Get("/eighth/required-ctor");

    public override ValueTask<RequiredCtorQuery> HandleAsync(RequiredCtorQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request);
}

public sealed class PopulateQuery
{
    public int Page { get; set; } = 1;

    public List<string> Tags { get; } = [];

    public int Id => 42;
}

public sealed class PopulateEndpoint : Endpoint<PopulateQuery, string>
{
    public override void Configure() => Get("/eighth/populate");

    public override ValueTask<string> HandleAsync(PopulateQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult($"{request.Page}:{string.Join(',', request.Tags)}:{request.Id}");
}

/// <summary>Regressions from review of PR #8 (ninth pass).</summary>
public sealed class NinthPassBindingTests
{
    [Test]
    public async Task OptionalRouteSegmentsKeepTheQueryFallback()
    {
        await using var app = await StartAsync(_ => { }, EndpointRegistration.Create<OptionalItemEndpoint>());
        using var client = Client(app);
        await Assert.That(await client.GetStringAsync("/optional?id=7")).IsEqualTo("""{"id":7}""");
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var parameters = document.RootElement.GetProperty("paths").GetProperty("/optional/{id}").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Select(parameter => $"{parameter.GetProperty("in").GetString()}:{parameter.GetProperty("name").GetString()}").ToArray();
        await Assert.That(parameters).IsEquivalentTo(["path:id", "query:id"]);
    }

    [Test]
    public async Task ValidatedRequiredMembersAreRequiredQueryParameters()
    {
        await using var app = await StartAsync(ValidationSetup.Enable, EndpointRegistration.Create<ValidatedQueryEndpoint>());
        using var client = Client(app);
        using var missing = await client.GetAsync("/ninth/validated");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var name = document.RootElement.GetProperty("paths").GetProperty("/ninth/validated").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "name");
        await Assert.That(name.GetProperty("required").GetBoolean()).IsTrue();
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints);
        configure(builder);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

[ValidatableType]
public sealed class ValidatedQuery
{
    [Required]
    public string Name { get; set; } = "";
}

public sealed class ValidatedQueryEndpoint : Endpoint<ValidatedQuery, string>
{
    public override void Configure() => Get("/ninth/validated");

    public override ValueTask<string> HandleAsync(ValidatedQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request.Name);
}

/// <summary>Regressions from review of PR #8 (tenth pass).</summary>
public sealed class TenthPassBindingTests
{
    [Test]
    public async Task RequiredBodyOnlyMembersMakeTheBodyRequired()
    {
        await using var app = await StartAsync(_ => { }, EndpointRegistration.Create<RequiredNestedEndpoint>());
        using var client = Client(app);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var body = document.RootElement.GetProperty("paths").GetProperty("/tenth/required-nested").GetProperty("get").GetProperty("requestBody");
        await Assert.That(body.GetProperty("required").GetBoolean()).IsTrue();
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints);
        configure(builder);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

public sealed class RequiredNestedQuery
{
    public int Page { get; init; } = 1;

    public required NestedFilter Filter { get; init; }
}

public sealed class RequiredNestedEndpoint : Endpoint<RequiredNestedQuery, string>
{
    public override void Configure() => Get("/tenth/required-nested");

    public override ValueTask<string> HandleAsync(RequiredNestedQuery request, CancellationToken cancellationToken) => ValueTask.FromResult(request.Filter.Name);
}

public sealed class RequiredIdQuery
{
    public required int Id { get; init; }
}

public sealed class RequiredOptionalIdEndpoint : Endpoint<RequiredIdQuery, string>
{
    public override void Configure() => Get("/tenth/optional/{id?}");

    public override ValueTask<string> HandleAsync(RequiredIdQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed class RequiredDefaultedIdEndpoint : Endpoint<RequiredIdQuery, string>
{
    public override void Configure() => Get("/tenth/defaulted/{id=5}");

    public override ValueTask<string> HandleAsync(RequiredIdQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed class PopulateBody
{
    public int Page { get; set; } = 1;

    public List<string> Tags { get; } = [];
}

public sealed class PopulateBodyEndpoint : Endpoint<PopulateBody, string>
{
    public override void Configure() => Post("/tenth/populate");

    public override ValueTask<string> HandleAsync(PopulateBody request, CancellationToken cancellationToken) =>
        ValueTask.FromResult($"{request.Page}:{string.Join(',', request.Tags)}");
}

public sealed record DataBody(int Page, Dictionary<string, int>? Data);

public sealed class DataBodyEndpoint : Endpoint<DataBody, string>
{
    public override void Configure() => Post("/tenth/data");

    public override ValueTask<string> HandleAsync(DataBody request, CancellationToken cancellationToken) =>
        ValueTask.FromResult($"{request.Page}:{string.Join(',', request.Data!.Select(pair => $"{pair.Key}={pair.Value}"))}");
}

/// <summary>Regressions from review of PR #8 (eleventh pass).</summary>
public sealed class EleventhPassBindingTests
{
    [Test]
    public async Task OptionalAndDefaultedSegmentsKeepTheirQueryFallbackUnderAGroup()
    {
        // ApiExplorer's RelativePath keeps {id?} and {id=5}; only the OpenAPI path key drops the markers.
        await using var app = await StartAsync(
            _ => { },
            mapInGroup: true,
            EndpointRegistration.Create<RequiredOptionalIdEndpoint>(),
            EndpointRegistration.Create<RequiredDefaultedIdEndpoint>());
        using var client = Client(app);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var path in new[] { "/tenants/{tenantId}/tenth/optional/{id}", "/tenants/{tenantId}/tenth/defaulted/{id}" })
        {
            var parameters = paths.GetProperty(path).GetProperty("get").GetProperty("parameters").EnumerateArray()
                .Select(parameter => $"{parameter.GetProperty("in").GetString()}:{parameter.GetProperty("name").GetString()}").ToArray();
            await Assert.That(parameters).IsEquivalentTo(["path:tenantId", "path:id", "query:id"]);
        }
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, bool mapInGroup, params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints);
        configure(builder);
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

/// <summary>Regressions from review of PR #8 (twelfth pass).</summary>
public sealed class TwelfthPassBindingTests
{
    [Test]
    public async Task TypedRouteValuesBindIndependentlyOfTheCulture()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints([EndpointRegistration.Create<PriceEndpoint>()]);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            // Middleware may store typed route values; formatting them must not follow fr-FR's "1,5".
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            context.Request.RouteValues["price"] = 1.5m;
            await next(context);
        });
        app.MapTerraScaleEndpoints();
        await app.StartAsync();
        using var client = Client(app);
        await Assert.That(await client.GetStringAsync("/twelfth/price")).IsEqualTo("1.5");
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints);
        configure(builder);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

public sealed record DeepBody(int Page, JsonElement Data);

public sealed class DeepBodyEndpoint : Endpoint<DeepBody, string>
{
    public override void Configure() => Post("/twelfth/deep");

    public override ValueTask<string> HandleAsync(DeepBody request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.Page.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed record PriceQuery(decimal Price);

public sealed class PriceEndpoint : Endpoint<PriceQuery, string>
{
    public override void Configure() => Get("/twelfth/price");

    public override ValueTask<string> HandleAsync(PriceQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.Price.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed class ReadOnlyIdsQuery
{
    public int Page { get; set; } = 1;

    public List<string> Tags { get; } = [];

    public IReadOnlyList<int> Ids { get; } = new List<int>();
}

public sealed class ReadOnlyIdsEndpoint : Endpoint<ReadOnlyIdsQuery, string>
{
    public override void Configure() => Get("/twelfth/read-only");

    public override ValueTask<string> HandleAsync(ReadOnlyIdsQuery request, CancellationToken cancellationToken) => ValueTask.FromResult("read-only");
}

/// <summary>Regressions from review of PR #8 (thirteenth pass).</summary>
public sealed class ThirteenthPassBindingTests
{
    [Test]
    public async Task RouteSlotsDescribedByApiExplorerUseTheBoundPropertySchema()
    {
        await using var app = await StartAsync(_ => { }, EndpointRegistration.Create<IdsEndpoint>());
        using var client = Client(app);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var ids = document.RootElement.GetProperty("paths").GetProperty("/ids/{ids}").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "ids");
        var schema = ids.GetProperty("schema");
        await Assert.That(schema.GetProperty("type").ToString()).Contains("array");
        await Assert.That(schema.GetProperty("items").GetProperty("type").ToString()).Contains("integer");
        await Assert.That(ids.GetProperty("required").GetBoolean()).IsTrue();
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints);
        configure(builder);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

public sealed class SetsQuery
{
    public HashSet<int> Ids { get; } = [];

    public ISet<string> Labels { get; } = new HashSet<string>();
}

public sealed class SetsEndpoint : Endpoint<SetsQuery, string>
{
    public override void Configure() => Get("/thirteenth/sets");

    public override ValueTask<string> HandleAsync(SetsQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult($"{string.Join(',', request.Ids.Order())}|{string.Join(',', request.Labels.Order())}");
}

/// <summary>Regressions from review of PR #8 (fourteenth pass).</summary>
public sealed class FourteenthPassBindingTests
{
    // Mirrors what System.Text.Json actually populates for a getter-only member under Populate
    // (probed directly): these collections are inputs, the others are not.
    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints);
        configure(builder);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
}

public sealed class CollectionsQuery
{
    public List<int> List { get; } = [];

    public HashSet<int> HashSet { get; } = [];

    public Queue<int> Queue { get; } = new();

    public Stack<int> Stack { get; } = new();

    public LinkedList<int> LinkedList { get; } = new();

    public SortedSet<int> SortedSet { get; } = [];

    public System.Collections.ObjectModel.Collection<int> Collection { get; } = [];

    public System.Collections.ObjectModel.ObservableCollection<int> Observable { get; } = [];

    public System.Collections.Concurrent.ConcurrentQueue<int> ConcurrentQueue { get; } = new();

    public System.Collections.Concurrent.ConcurrentStack<int> ConcurrentStack { get; } = new();

    public ICollection<int> ICollection { get; } = new List<int>();

    public IList<int> IList { get; } = new List<int>();

    public ISet<int> ISet { get; } = new HashSet<int>();

    // Not populated by System.Text.Json, so not inputs.
    public System.Collections.Concurrent.ConcurrentBag<int> Bag { get; } = [];

    public IReadOnlyList<int> ReadOnlyList { get; } = new List<int>();

    public IEnumerable<int> Enumerable { get; } = new List<int>();

    public System.Collections.Immutable.ImmutableList<int> Immutable { get; } = [];
}

public sealed class CollectionsEndpoint : Endpoint<CollectionsQuery, string>
{
    public override void Configure() => Get("/fourteenth/collections");

    public override ValueTask<string> HandleAsync(CollectionsQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult($"{string.Join(',', request.Queue)}|{string.Join(',', request.Stack)}|{string.Join(',', request.ConcurrentQueue)}");
}
