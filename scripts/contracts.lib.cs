// Fetch the pinned TerraBase contract and generate Go contract metadata.
// Port of the former scripts/contracts.py — same caps, same fail-closed rules,
// same generated output (modulo the generator name in the header comment).
//
// The producer bundle is authoritative. A local producer checkout is accepted
// only when explicitly supplied for pre-publication development. Normal and CI
// invocations require an immutable package URL. No consumer (and especially no
// other SDK) is ever used as a contract source.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraScale.Ci;

public sealed class ContractError(string message) : Exception(message);

public static class Contracts
{
    public static readonly string[] Required =
    [
        "manifest.json",
        "openapi.json",
        "msgpack/terrascale.sync.v1.msgpack.json",
    ];

    public static readonly string[] Conformance =
    [
        "conformance/http.json",
        "conformance/reactive.json",
        "conformance/msgpack/message-vectors.json",
        "conformance/msgpack/hostile-vectors.json",
    ];

    public const int MaxArchiveMembers = 256;
    public const int MaxArchiveEntryBytes = 16 * 1024 * 1024;
    public const int MaxArchiveTotalBytes = 64 * 1024 * 1024;
    public const int MaxArchiveBytes = 80 * 1024 * 1024;

    [DoesNotReturn]
    public static void Die(string message) => throw new ContractError($"contracts: {message}");

    private static IEnumerable<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name);

    public static bool IsVersion(string value) =>
        value.Length >= 2 && value[0] == 'v' && value[1] >= '1' && value[1] <= '9'
        && value[1..].All(char.IsAsciiDigit);

    // ---------- filesystem guards ----------

    // Full x86_64 glibc `struct stat` (144 bytes) — the native side writes the
    // whole struct, so the marshaled layout must match to the byte.
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemStat
    {
        public ulong Dev;
        public ulong Ino;
        public ulong NLink;
        public uint Mode;
        public uint Uid;
        public uint Gid;
        public uint Pad;
        public ulong RDev;
        public long Size;
        public long BlkSize;
        public long Blocks;
        public long AtTimeSec;
        public long AtTimeNsec;
        public long MTimeSec;
        public long MTimeNsec;
        public long CTimeSec;
        public long CTimeNsec;
        public ulong Reserved0;
        public ulong Reserved1;
        public ulong Reserved2;
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int StatFollow(string path, out SystemStat buffer);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int StatLink(string path, out SystemStat buffer);

    private const uint SIfMt = 0xF000;
    private const uint SIfReg = 0x8000;
    private const uint SIfLnk = 0xA000;
    private const uint SIfDir = 0x4000;

    public static void RegularFile(string path, string label)
    {
        if (StatLink(path, out var linkInfo) != 0)
        {
            Die($"{label} is unavailable: errno {Marshal.GetLastWin32Error()}");
        }
        if ((linkInfo.Mode & SIfMt) != SIfReg)
        {
            Die($"{label} is not a regular file");
        }
        if (StatFollow(path, out var info) != 0 || info.NLink != 1)
        {
            Die($"{label} is a hard link");
        }
    }

    public static bool IsSymlink(string path) => StatLink(path, out var info) == 0 && (info.Mode & SIfMt) == SIfLnk;

    public static void RejectLinks(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (StatLink(path, out var info) != 0)
            {
                Die($"contract input is unavailable: {path}");
            }
            if ((info.Mode & SIfMt) == SIfLnk)
            {
                Die($"contract input contains a symlink: {path}");
            }
            if ((info.Mode & SIfMt) == SIfReg && info.NLink != 1)
            {
                Die($"contract input contains a hard link: {path}");
            }
            if ((info.Mode & SIfMt) is not (SIfReg or SIfDir))
            {
                Die($"contract input contains a non-file entry: {path}");
            }
        }
    }

    // ---------- JSON (document order preserved, duplicate keys rejected) ----------

    public static JsonDocument LoadJson(string path)
    {
        RegularFile(path, path);
        try
        {
            using var stream = File.OpenRead(path);
            var document = JsonDocument.Parse(stream);
            RejectDuplicateKeys(document.RootElement, path);
            return document;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Die($"invalid JSON in {path}: {error.Message}");
            throw;
        }
    }

    private static void RejectDuplicateKeys(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>();
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        Die($"duplicate key '{property.Name}' in {path}");
                    }
                    RejectDuplicateKeys(property.Value, path);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    RejectDuplicateKeys(item, path);
                }
                break;
        }
    }

    // ---------- digests ----------

    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Blake3Hex(byte[] data)
    {
        using var hasher = Blake3.Hasher.New();
        hasher.Update(data);
        return hasher.Finalize().ToString();
    }

    // ---------- bundle validation ----------

    public static (JsonElement Manifest, SortedDictionary<string, string> Digests) ValidateBundle(string root, string version)
    {
        RejectLinks(root);
        using var manifestDocument = LoadJson(Path.Join(root, "manifest.json"));
        var manifest = manifestDocument.RootElement;
        if (manifest.ValueKind != JsonValueKind.Object || !manifest.TryGetProperty("schemaVersion", out var schemaVersion)
            || schemaVersion.ValueKind != JsonValueKind.Number || schemaVersion.GetDouble() != 1)
        {
            Die("manifest schemaVersion is not 1");
        }
        string? manifestVersion = null;
        if (manifest.ValueKind == JsonValueKind.Object && manifest.TryGetProperty("contractVersion", out var contractVersion)
            && contractVersion.ValueKind == JsonValueKind.String)
        {
            manifestVersion = contractVersion.GetString();
        }
        if (manifestVersion != version)
        {
            Die($"manifest contractVersion is '{manifestVersion}', expected '{version}'");
        }
        if (!manifest.TryGetProperty("artifacts", out var artifacts)
            || artifacts.ValueKind != JsonValueKind.Array
            || artifacts.GetArrayLength() == 0)
        {
            Die("manifest has no artifacts");
        }
        var digests = new SortedDictionary<string, string>();
        foreach (var entry in artifacts.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                Die("manifest artifact is not an object");
            }
            var relative = entry.TryGetProperty("path", out var pathValue) ? pathValue.GetString() : null;
            var expected = entry.TryGetProperty("blake3", out var digestValue) ? digestValue.GetString() : null;
            var invalid = relative is null || relative.Length == 0
                || relative.StartsWith('/')
                || relative.Split('/').Contains("..")
                || expected is null || expected.Length != 64;
            if (invalid)
            {
                Die($"manifest artifact has an invalid path or digest: '{relative}'");
            }
            if (digests.ContainsKey(relative!))
            {
                Die($"manifest repeats artifact path: {relative}");
            }
            var artifactPath = Path.Join(root, relative!);
            RegularFile(artifactPath, relative!);
            var actual = Blake3Hex(File.ReadAllBytes(artifactPath));
            if (actual != expected)
            {
                Die($"manifest digest mismatch for {relative}: {actual} != {expected}");
            }
            digests[relative!] = expected!;
        }
        foreach (var required in Required)
        {
            RegularFile(Path.Join(root, required), required);
            if (required != "manifest.json" && !digests.ContainsKey(required))
            {
                Die($"manifest does not pin required artifact {required}");
            }
        }
        return (manifest.Clone(), digests);
    }

    // ---------- archive download + extraction ----------

    public static byte[] DownloadArchive(string url)
    {
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) });
            client.Timeout = TimeSpan.FromSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("terrascale-ci/1");
            using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var stream = response.Content.ReadAsStream();
            using var buffer = new MemoryStream();
            var chunk = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxArchiveBytes)
                {
                    Die("downloaded contract archive is too large");
                }
            }
            return buffer.ToArray();
        }
        catch (ContractError)
        {
            throw;
        }
        catch (Exception error)
        {
            Die($"could not download pinned contract URL '{url}': {error.Message}");
            throw;
        }
    }

    /// python SafeNormalizedName: reject empty/NUL/backslash, absolute, "..",
    /// non-normalized; returns the posix-normalized path.
    private static string NormalizeEntryName(string name)
    {
        if (name.Length == 0 || name.Contains('\0') || name.Contains('\\'))
        {
            Die("downloaded contract archive contains an invalid entry name");
        }
        if (name.StartsWith('/') || name.Split('/').Contains(".."))
        {
            Die($"downloaded contract archive contains path traversal: '{name}'");
        }
        var parts = name.Split('/').Where(part => part.Length > 0 && part != ".").ToArray();
        return string.Join("/", parts);
    }

    public static string UnpackUrl(string url, string destination, Func<string, byte[]>? download = null)
    {
        if (url.Length == 0)
        {
            Die(
                "contract v1 is not published in the registry; set CONTRACT_SOURCE " +
                "to a local terrabase/contracts directory or CONTRACT_URL to the " +
                "immutable v1 package URL (registry blocker)");
        }
        download ??= DownloadArchive;
        var payload = download(url);
        return UnpackArchive(payload, destination);
    }

    public static string UnpackArchive(byte[] payload, string destination)
    {
        var extracted = Path.Join(destination, "source");
        Directory.CreateDirectory(extracted);
        try
        {
            using (var first = new MemoryStream(payload))
            using (var reader = new TarReader(first))
            {
                TarEntry? entry;
                var seen = new HashSet<string>();
                var members = 0;
                long total = 0;
                while ((entry = reader.GetNextEntry()) is not null)
                {
                    if (++members > MaxArchiveMembers)
                    {
                        Die("downloaded contract archive has too many entries");
                    }
                    var normalized = NormalizeEntryName(entry.Name);
                    if (!seen.Add(normalized))
                    {
                        Die($"downloaded contract archive repeats entry: '{entry.Name}'");
                    }
                    var target = Path.GetFullPath(Path.Join(extracted, normalized));
                    var extractedRoot = Path.GetFullPath(extracted);
                    if (target != extractedRoot && !target.StartsWith(extractedRoot + Path.DirectorySeparatorChar))
                    {
                        Die("downloaded contract archive contains an unsafe path");
                    }
                    if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                    {
                        Die($"downloaded contract archive contains a link: '{entry.Name}'");
                    }
                    if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile))
                    {
                        Die($"downloaded contract archive contains a special entry: '{entry.Name}'");
                    }
                    if (entry.EntryType == TarEntryType.RegularFile)
                    {
                        if (entry.Length < 0 || entry.Length > MaxArchiveEntryBytes)
                        {
                            Die($"downloaded contract archive entry is too large: '{entry.Name}'");
                        }
                        total += entry.Length;
                        if (total > MaxArchiveTotalBytes)
                        {
                            Die("downloaded contract archive is too large");
                        }
                    }
                }
            }
            using (var second = new MemoryStream(payload))
            using (var reader = new TarReader(second))
            {
                TarEntry? entry;
                while ((entry = reader.GetNextEntry()) is not null)
                {
                    var normalized = NormalizeEntryName(entry.Name);
                    var target = Path.Join(extracted, normalized);
                    if (entry.EntryType == TarEntryType.Directory)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = entry.DataStream!;
                    using var output = File.Create(target);
                    input.CopyTo(output);
                }
            }
        }
        catch (ContractError)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        {
            Die($"downloaded contract is not a tar archive: {error.Message}");
        }
        var candidates = new List<string> { extracted };
        candidates.AddRange(Directory.EnumerateDirectories(extracted));
        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Join(candidate, "manifest.json")))
            {
                return candidate;
            }
        }
        Die("downloaded contract archive has no manifest.json");
        throw new ContractError("unreachable");
    }

    // ---------- conformance corpus ----------

    /// Copy only the producer's published conformance corpus. E-1 owns this
    /// corpus. A consumer must never silently retain a fixture from an older
    /// checkout or copy one from another SDK.
    public static void CopyConformance(string source, string destination)
    {
        var missing = Conformance.Where(relative => !File.Exists(Path.Join(source, relative))).ToArray();
        if (missing.Length > 0)
        {
            Die(
                "producer conformance corpus is unavailable (missing " + string.Join(", ", missing) +
                "); E-1 must publish these versioned conformance artifacts before ts-go can regenerate contract v1");
        }
        foreach (var relative in Conformance)
        {
            var sourceFile = Path.Join(source, relative);
            RegularFile(sourceFile, $"producer conformance artifact {relative}");
            var destinationFile = Path.Join(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: true);
            // Keep the established Go MessagePack test paths as a generated
            // compatibility mirror; both copies come from this producer artifact.
            if (relative.StartsWith("conformance/msgpack/"))
            {
                var mirror = Path.Join(destination, "msgpack", Path.GetFileName(relative));
                Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
                File.Copy(sourceFile, mirror, overwrite: true);
            }
        }
    }

    // ---------- OpenAPI / MessagePack metadata ----------

    public static string SchemaType(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        if (schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
        {
            var text = reference.GetString()!;
            if (text.StartsWith("#/components/schemas/"))
            {
                return text.Split('/')[^1];
            }
        }
        if (schema.TryGetProperty("type", out var kind))
        {
            if (kind.ValueKind == JsonValueKind.String && kind.GetString() == "array")
            {
                var items = schema.TryGetProperty("items", out var itemsElement) ? itemsElement : default;
                var inner = itemsElement.ValueKind == JsonValueKind.Object ? SchemaType(itemsElement) : "";
                return "[]" + (inner.Length > 0 ? inner : "any");
            }
            if (kind.ValueKind == JsonValueKind.Array)
            {
                var first = kind.EnumerateArray()
                    .Where(item => (item.GetString() ?? "") != "null")
                    .Select(item => SchemaType(JsonValueToObject(item)))
                    .FirstOrDefault(type => type.Length > 0);
                return first ?? "any";
            }
            var name = kind.ValueKind == JsonValueKind.String ? kind.GetString() : null;
            return name switch
            {
                "string" => "string",
                "integer" => "int64",
                "number" => "float64",
                "boolean" => "bool",
                "object" => "map[string]any",
                _ => "any",
            };
        }
        return "";
    }

    private static JsonElement JsonValueToObject(JsonElement element) => element;

    public sealed record OperationMetadata(string OperationId, string Method, string Path, string RequestModel, string ResponseModel);
    public sealed record RouteMetadata(string Method, string Path, string OperationId);
    public sealed record ModelMetadata(string Name, string[] Properties);
    public sealed record FrameMetadata(string Name, uint Tag, string Direction, string[] Fields);

    public static (List<OperationMetadata> Operations, List<RouteMetadata> Routes, List<ModelMetadata> Models)
        OpenApiMetadata(JsonElement openapi)
    {
        var paths = openapi.ValueKind == JsonValueKind.Object && openapi.TryGetProperty("paths", out var pathsValue)
            && pathsValue.ValueKind == JsonValueKind.Object ? pathsValue : default;
        JsonElement schemas = default;
        if (openapi.ValueKind == JsonValueKind.Object && openapi.TryGetProperty("components", out var components)
            && components.ValueKind == JsonValueKind.Object
            && components.TryGetProperty("schemas", out var schemasValue)
            && schemasValue.ValueKind == JsonValueKind.Object)
        {
            schemas = schemasValue;
        }
        if (paths.ValueKind == JsonValueKind.Undefined || schemas.ValueKind == JsonValueKind.Undefined)
        {
            Die("OpenAPI artifact has no usable paths/components.schemas metadata");
        }
        var operations = new List<OperationMetadata>();
        var routes = new List<RouteMetadata>();
        string[] methods = ["get", "post", "put", "patch", "delete", "options", "head", "trace"];
        foreach (var path in PropertyNames(paths).Order())
        {
            if (!paths.TryGetProperty(path, out var pathItem) || pathItem.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            foreach (var method in methods)
            {
                if (!pathItem.TryGetProperty(method, out var operation) || operation.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var operationId = operation.TryGetProperty("operationId", out var idValue) ? idValue.GetString() : null;
                if (string.IsNullOrEmpty(operationId))
                {
                    Die($"OpenAPI {method} {path} has no operationId");
                }
                var requestModel = "";
                if (operation.TryGetProperty("requestBody", out var requestBody)
                    && requestBody.ValueKind == JsonValueKind.Object
                    && requestBody.TryGetProperty("content", out var requestContent)
                    && requestContent.ValueKind == JsonValueKind.Object)
                {
                    foreach (var content in requestContent.EnumerateObject())
                    {
                        if (content.Value.ValueKind == JsonValueKind.Object)
                        {
                            var schema = content.Value.TryGetProperty("schema", out var schemaValue) ? schemaValue : default;
                            requestModel = SchemaType(schema);
                            if (requestModel.Length > 0)
                            {
                                break;
                            }
                        }
                    }
                }
                var responseModel = "";
                if (operation.TryGetProperty("responses", out var responses)
                    && responses.ValueKind == JsonValueKind.Object)
                {
                    foreach (var status in PropertyNames(responses).Order())
                    {
                        if (!status.StartsWith('2'))
                        {
                            continue;
                        }
                        if (responses.TryGetProperty(status, out var response)
                            && response.ValueKind == JsonValueKind.Object
                            && response.TryGetProperty("content", out var responseContent)
                            && responseContent.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var content in responseContent.EnumerateObject())
                            {
                                if (content.Value.ValueKind == JsonValueKind.Object)
                                {
                                    var schema = content.Value.TryGetProperty("schema", out var schemaValue) ? schemaValue : default;
                                    responseModel = SchemaType(schema);
                                    if (responseModel.Length > 0)
                                    {
                                        break;
                                    }
                                }
                            }
                        }
                        if (responseModel.Length > 0)
                        {
                            break;
                        }
                    }
                }
                operations.Add(new OperationMetadata(operationId!, method.ToUpperInvariant(), path, requestModel, responseModel));
                routes.Add(new RouteMetadata(method.ToUpperInvariant(), path, operationId!));
            }
        }
        var models = new List<ModelMetadata>();
        foreach (var name in PropertyNames(schemas).Order())
        {
            var schema = schemas.TryGetProperty(name, out var schemaValue) ? schemaValue : default;
            var properties = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var propertiesValue)
                && propertiesValue.ValueKind == JsonValueKind.Object
                    ? PropertyNames(propertiesValue).Order().ToArray()
                    : [];
            models.Add(new ModelMetadata(name, properties));
        }
        var orderedOperations = operations
            .OrderBy(item => item.OperationId, StringComparer.Ordinal)
            .ThenBy(item => item.Method, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal).ToList();
        var orderedRoutes = routes
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Method, StringComparer.Ordinal)
            .ThenBy(item => item.OperationId, StringComparer.Ordinal).ToList();
        return (orderedOperations, orderedRoutes, models);
    }

    public static List<FrameMetadata> MsgpackMetadata(JsonElement manifest)
    {
        JsonElement messages = default;
        if (manifest.ValueKind != JsonValueKind.Object
            || !manifest.TryGetProperty("messages", out messages)
            || messages.ValueKind != JsonValueKind.Array
            || messages.GetArrayLength() == 0)
        {
            Die("MessagePack artifact has no messages metadata");
        }
        var result = new List<FrameMetadata>();
        foreach (var message in messages.EnumerateArray())
        {
            var name = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString() : null;
            if (message.ValueKind != JsonValueKind.Object || name is null)
            {
                Die("MessagePack message has no stable name");
            }
            if (!message.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            {
                Die($"MessagePack message {name} has no fields");
            }
            var fieldNames = new List<string>();
            foreach (var field in fields.EnumerateArray())
            {
                var fieldName = field.ValueKind == JsonValueKind.Object && field.TryGetProperty("name", out var fieldNameValue)
                    ? fieldNameValue.GetString() : null;
                if (fieldName is null)
                {
                    Die($"MessagePack message {name} has an invalid field");
                }
                fieldNames.Add(fieldName);
            }
            long tag = 0;
            if (!message.TryGetProperty("tag", out var tagValue)
                || !tagValue.TryGetInt64(out tag) || tag < 1)
            {
                Die($"MessagePack message {name} has an invalid tag");
            }
            var direction = message.TryGetProperty("direction", out var directionValue)
                && directionValue.ValueKind == JsonValueKind.String ? directionValue.GetString() : null;
            if (direction is null)
            {
                Die($"MessagePack message {name} has an invalid direction");
            }
            result.Add(new FrameMetadata(name!, (uint)tag, direction!, [.. fieldNames]));
        }
        return result.OrderBy(item => item.Tag).ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
    }

    // ---------- code generation ----------

    /// python json.dumps(ensure_ascii=True) string escaping, byte for byte.
    public static string GoString(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    public static string GoStrings(string[] values) =>
        "[]string{" + string.Join(", ", values.Select(GoString)) + "}";

    public static string RenderContractGo(JsonElement openapi, JsonElement msgpack, string version,
        SortedDictionary<string, string> digests)
    {
        var (operations, routes, models) = OpenApiMetadata(openapi);
        var frames = MsgpackMetadata(msgpack);
        var lines = new List<string>
        {
            "// Code generated by scripts/contracts.cs; DO NOT EDIT.",
            "// Contract metadata is derived only from the pinned TerraBase bundle.",
            "package sdk", "",
            "// ContractMetadata describes one generated OpenAPI operation.",
            "type ContractMetadata struct {", "\tOperationID string", "\tMethod string", "\tPath string", "\tRequestModel string", "\tResponseModel string", "}",
            "", "// ContractRoute describes one generated route template.",
            "type ContractRoute struct {", "\tMethod string", "\tPath string", "\tOperationID string", "}",
            "", "// ContractModel describes one generated OpenAPI schema model.",
            "type ContractModel struct {", "\tName string", "\tProperties []string", "}",
            "", "// ContractFrame describes one generated MessagePack message.",
            "type ContractFrame struct {", "\tName string", "\tTag uint32", "\tDirection string", "\tFields []string", "}",
            "", "const (",
            $"\tContractVersion = {GoString(version)}",
            $"\tContractOpenAPIBLAKE3 = {GoString(digests["openapi.json"])}",
            $"\tContractMessagePackBLAKE3 = {GoString(digests["msgpack/terrascale.sync.v1.msgpack.json"])}",
            ")", "",
            "var ContractOperations = []ContractMetadata{",
        };
        foreach (var item in operations)
        {
            lines.Add("\t{" + string.Join(", ",
            [
                GoString(item.OperationId), GoString(item.Method), GoString(item.Path),
                GoString(item.RequestModel), GoString(item.ResponseModel),
            ]) + "},");
        }
        lines.AddRange(["}", "", "var ContractRoutes = []ContractRoute{"]);
        foreach (var item in routes)
        {
            lines.Add("\t{" + string.Join(", ", [GoString(item.Method), GoString(item.Path), GoString(item.OperationId)]) + "},");
        }
        lines.AddRange(["}", "", "var ContractModels = []ContractModel{"]);
        foreach (var item in models)
        {
            lines.Add("\t{" + GoString(item.Name) + ", " + GoStrings(item.Properties) + "},");
        }
        lines.AddRange(["}", "", "var ContractFrames = []ContractFrame{"]);
        foreach (var item in frames)
        {
            lines.Add("\t{" + GoString(item.Name) + $", {item.Tag}, " + GoString(item.Direction) + ", " + GoStrings(item.Fields) + "},");
        }
        lines.AddRange(["}", ""]);
        return Gofmt(string.Join("\n", lines));
    }

    private static string Gofmt(string source)
    {
        var start = new ProcessStartInfo("gofmt")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        process.StandardInput.Write(source);
        process.StandardInput.Close();
        var formatted = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Die($"could not format generated Go contract metadata: {error.Trim()}");
        }
        return formatted;
    }

    public static string RenderGenerated(string destination, JsonElement manifest, SortedDictionary<string, string> digests)
    {
        var corpusSha = Sha256Hex(File.ReadAllBytes(Path.Join(destination, "msgpack/message-vectors.json")));
        var hostileSha = Sha256Hex(File.ReadAllBytes(Path.Join(destination, "msgpack/hostile-vectors.json")));
        return
            $$"""
            // Code generated by scripts/contracts.cs; DO NOT EDIT.
            // Contract artifacts are verified by conformance_contract_test.go.
            package sdk

            const (
            	pinnedContractVersion     = {{GoString(manifest.GetProperty("contractVersion").GetString()!)}}
            	pinnedOpenAPIBLAKE3       = {{GoString(digests["openapi.json"])}}
            	pinnedMessagePackBLAKE3   = {{GoString(digests["msgpack/terrascale.sync.v1.msgpack.json"])}}
            	pinnedMessageVectorSHA256 = {{GoString(corpusSha)}}
            	pinnedHostileVectorSHA256 = {{GoString(hostileSha)}}
            )
            """ + "\n";
    }

    // ---------- install ----------

    /// Remove only a directory we created directly under the known parent.
    public static void SafeRemoveTree(string path, string parent, string prefix)
    {
        if (Path.GetDirectoryName(path) != parent || !Path.GetFileName(path).StartsWith(prefix))
        {
            Die($"refusing to remove an unresolved path: {path}");
        }
        if (StatLink(path, out var info) != 0 || (info.Mode & SIfMt) is not (SIfDir) || StatFollow(path, out _) != 0)
        {
            Die($"refusing to remove an unsafe path: {path}");
        }
        Directory.Delete(path, recursive: true);
    }

    public static void UnlinkIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
