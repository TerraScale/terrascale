using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Validation;

namespace TerraScale.Endpoints.Example.Endpoints;

public sealed class CreateWidgetEndpoint(WidgetStore store) : Endpoint<CreateWidgetRequest, Widget>
{
    public override void Configure()
    {
        Post("/widgets");
        Name("create-widget");
        Summary("Creates a widget");
        Description("JSON request and response with DI.");
        Tags("Widgets");
    }

    protected override async IAsyncEnumerable<ValidationResult> ValidateAsync(
        CreateWidgetRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask; // A real store would await a database query here.
        if (store.NameExists(request.Name))
        {
            yield return new($"A widget named '{request.Name}' already exists.", [nameof(request.Name)]);
        }
    }

    public override ValueTask<Widget> HandleAsync(CreateWidgetRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(store.Create(request.Name));
}

[ValidatableType]
public sealed record CreateWidgetRequest([property: Required, StringLength(50, MinimumLength = 1)] string Name);
