using System.Text.Json.Serialization;

namespace TerraScale.DotNet.Native;

/// <summary>
/// Source-generated JSON metadata for the native wire tree, which is built as nested objects (trimming and native AOT
/// safe). Only the runtime types that tree holds are registered; the default options keep the bytes unchanged.
/// </summary>
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(uint[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
internal partial class NativeJsonContext : JsonSerializerContext;
