using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
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
