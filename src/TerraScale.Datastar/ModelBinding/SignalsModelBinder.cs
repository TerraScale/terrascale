using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TerraScale.Datastar.DependencyInjection;

namespace TerraScale.Datastar.ModelBinding;

public sealed class SignalsModelBinder(
    ILogger<SignalsModelBinder> logger,
    IDatastarService signalsReader,
    IOptions<DatastarJsonOptions> options) : IModelBinder
{
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var source = (DatastarSignalsBindingSource)bindingContext.BindingSource!;
        var serializerOptions = source.JsonSerializerOptions ?? options.Value.SignalsJsonSerializerOptions;
        try
        {
            using var document = await ReadSignalsAsync(bindingContext).ConfigureAwait(false);
            var value = BindValue(document.RootElement, bindingContext, source, serializerOptions);
            bindingContext.Result = ModelBindingResult.Success(value);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Invalid Datastar signals payload.");
            bindingContext.Result = ModelBindingResult.Failed();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            bindingContext.Result = ModelBindingResult.Failed();
        }
    }

    private async ValueTask<JsonDocument> ReadSignalsAsync(ModelBindingContext context)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsDelete(request.Method))
            return JsonDocument.Parse(await signalsReader.ReadSignalsAsync(context.HttpContext.RequestAborted).ConfigureAwait(false) ?? "{}");
        return await JsonDocument.ParseAsync(signalsReader.GetSignalsStream(), cancellationToken: context.HttpContext.RequestAborted).ConfigureAwait(false);
    }

    private static object? BindValue(JsonElement root, ModelBindingContext context, DatastarSignalsBindingSource source, JsonSerializerOptions options)
    {
        var path = string.IsNullOrWhiteSpace(source.BindingPath) ? context.FieldName : source.BindingPath;
        if (context.ModelType.IsValueType || context.ModelType == typeof(string))
        {
            var element = GetPath(root, path);
            return element is { } value
                ? value.Deserialize(context.ModelType, options)
                : context.ModelType.IsValueType ? Activator.CreateInstance(context.ModelType) : null;
        }

        var selected = string.IsNullOrWhiteSpace(source.BindingPath) ? root : GetPath(root, source.BindingPath);
        return selected?.Deserialize(context.ModelType, options);
    }

    private static JsonElement? GetPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        return current;
    }
}

public sealed class SignalsModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context) =>
        context.BindingInfo?.BindingSource?.DisplayName == DatastarSignalsBindingSource.BindingSourceName
            ? new BinderTypeModelBinder(typeof(SignalsModelBinder))
            : null;
}
