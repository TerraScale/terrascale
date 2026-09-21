using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace TerraScale.Datastar.ModelBinding;

public sealed class DatastarSignalsBindingSource(string path, JsonSerializerOptions? options)
    : BindingSource(BindingSourceName, BindingSourceName, isGreedy: true, isFromRequest: true)
{
    public const string BindingSourceName = "DatastarSignalsSource";
    public string BindingPath { get; } = path;
    public JsonSerializerOptions? JsonSerializerOptions { get; } = options;
}
