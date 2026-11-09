using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.FileStorage;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(FileBucket))]
[JsonSerializable(typeof(CreateFileBucketRequest))]
[JsonSerializable(typeof(FileBucketRulesRequest))]
[JsonSerializable(typeof(CreateFileUploadRequest))]
[JsonSerializable(typeof(FileObject))]
[JsonSerializable(typeof(FileSignedUrl))]
[JsonSerializable(typeof(FileBucketPage))]
[JsonSerializable(typeof(FileObjectPage))]
[JsonSerializable(typeof(FileAccessRules))]
internal sealed partial class FileStorageJsonContext : JsonSerializerContext;
