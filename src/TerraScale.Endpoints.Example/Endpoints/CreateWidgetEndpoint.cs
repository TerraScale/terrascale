using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Validation;

namespace TerraScale.Endpoints.Example.Endpoints;

public sealed class CreateWidgetEndpoint(WidgetStore store) : Endpoint<CreateWidgetRequest, IResult>
{
    public override void Configure()
    {
        Post("/widgets");
        Name("create-widget");
        Summary("Creates a widget");
        Description("JSON request and response with DI.");
        Tags("Widgets");
        Produces<Widget>();
        Produces(StatusCodes.Status409Conflict);
    }

    protected override async IAsyncEnumerable<ValidationResult> ValidateAsync(
        CreateWidgetRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Early check for a friendly 400; TryCreate still guards concurrent requests with a 409.
        await Task.CompletedTask; // A real store would await a database query here.
        if (store.NameExists(request.Name))
        {
            yield return new($"A widget named '{request.Name}' already exists.", [nameof(request.Name)]);
        }
    }

    public override ValueTask<IResult> HandleAsync(CreateWidgetRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IResult>(store.TryCreate(request.Name, out var widget) ? TypedResults.Ok(widget) : TypedResults.Conflict());
}

[ValidatableType]
public sealed record CreateWidgetRequest([property: Required, StringLength(50, MinimumLength = 1)] string Name);
