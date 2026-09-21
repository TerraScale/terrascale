// Install and generate the pinned TerraBase v1 contract for the Rust SDK.
// Port of the former scripts/contracts.py — same caps, same fail-closed rules,
// and a byte-compatible render of src/generated_contract.rs (the output then
// passes through `rustfmt --edition 2021`, exactly as before).
//
// The producer bundle is authoritative. A local producer is accepted only when
// CONTRACT_SOURCE is explicitly supplied (pre-publication development). CI and
// normal consumer builds fail closed until an immutable package URL and its
// archive digest are configured. MessagePack conformance fixtures are always
// discovered in that producer bundle; they are never copied from another SDK.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Ci;

public sealed class ContractError(string message) : Exception(message);

public static partial class Contracts
{
    public static readonly string[] Required =
    [
        "openapi.json",
        "msgpack/terrascale.sync.v1.msgpack.json",
        "proto/terrabase/v1/engine.proto",
        "proto/terrabase/v1/family.proto",
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

    public static string SafeRelative(string value, string label)
    {
        if (value.Length == 0 || value.Contains('\0') || value.Contains('\\'))
        {
            Die($"{label} has an invalid path");
        }
        if (value.StartsWith('/') || value.Split('/').Contains("..") || value != value.TrimEnd('/'))
        {
            Die($"{label} is not normalized: '{value}'");
        }
        return value;
    }

    public static (JsonElement Manifest, SortedDictionary<string, string> Digests) ValidateBundle(string root, string version)
    {
        RejectLinks(root);
        using var manifestDocument = LoadJson(Path.Join(root, "manifest.json"));
        var manifest = manifestDocument.RootElement;
        if (!NumberEquals(manifest, "schemaVersion", 1))
        {
            Die("manifest schemaVersion is not 1");
        }
        var manifestVersion = OptionalString(manifest, "contractVersion");
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
            var relative = OptionalString(entry, "path");
            var expected = OptionalString(entry, "blake3");
            var invalid = relative is null || relative.Length == 0
                || relative.StartsWith('/')
                || relative.Split('/').Contains("..")
                || expected is null || expected.Length != 64;
            if (invalid)
            {
                Die($"manifest artifact has an invalid path or digest: '{relative}'");
            }
            var normalized = SafeRelative(relative!, "manifest artifact path");
            if (digests.ContainsKey(normalized))
            {
                Die($"manifest repeats artifact path: {normalized}");
            }
            var path = Path.Join(root, normalized);
            RegularFile(path, normalized);
            var actual = Blake3Hex(File.ReadAllBytes(path));
            if (actual != expected)
            {
                Die($"manifest digest mismatch for {normalized}: {actual} != {expected}");
            }
            digests[normalized] = expected!;
        }
        foreach (var relative in Required.Concat(Conformance))
        {
            RegularFile(Path.Join(root, relative), relative);
            if (!digests.ContainsKey(relative))
            {
                Die($"manifest does not pin required artifact {relative}");
            }
        }
        return (manifest.Clone(), digests);
    }

    /// Validate fixtures discovered in the producer's own conformance tree.
    public static SortedDictionary<string, string> ValidateCorpus(string root)
    {
        var manifestPath = Path.Join(root, "msgpack/terrascale.sync.v1.msgpack.json");
        var manifestSha = Sha256Hex(File.ReadAllBytes(manifestPath));
        var result = new SortedDictionary<string, string>();
        foreach (var relative in Conformance)
        {
            var path = Path.Join(root, relative);
            RegularFile(path, $"producer conformance fixture {relative}");
            if (new FileInfo(path).Length > MaxArchiveEntryBytes)
            {
                Die($"conformance fixture is too large: {relative}");
            }
            using var document = LoadJson(path);
            var value = document.RootElement;
            if (relative.EndsWith("message-vectors.json"))
            {
                if (OptionalString(value, "protocol") != "terrascale.sync.v1.msgpack"
                    || OptionalString(value, "version") != "1.0")
                {
                    Die("producer MessagePack corpus identity is not 1.0");
                }
                if (OptionalString(value, "manifestSha256") != manifestSha)
                {
                    Die("producer MessagePack corpus pins a different manifest SHA-256");
                }
                if (!value.TryGetProperty("vectors", out var vectors)
                    || vectors.ValueKind != JsonValueKind.Array
                    || vectors.GetArrayLength() < 11)
                {
                    Die("producer MessagePack corpus is incomplete");
                }
                foreach (var vector in vectors.EnumerateArray())
                {
                    var name = OptionalString(vector, "name");
                    if (vector.ValueKind != JsonValueKind.Object || name is null)
                    {
                        Die("producer MessagePack vector has no name");
                    }
                    var hex = OptionalString(vector, "hex") ?? "";
                    try
                    {
                        Convert.FromHexString(hex);
                    }
                    catch (FormatException)
                    {
                        Die($"producer MessagePack vector '{name}' is not hex");
                    }
                }
            }
            else if (relative.EndsWith("hostile-vectors.json"))
            {
                if (!value.TryGetProperty("vectors", out var vectors)
                    || vectors.ValueKind != JsonValueKind.Array
                    || vectors.GetArrayLength() < 10)
                {
                    Die("producer hostile MessagePack corpus is incomplete");
                }
            }
            else if (relative.EndsWith("http.json"))
            {
                if (!NumberEquals(value, "schemaVersion", 1)
                    || !value.TryGetProperty("operations", out var operations)
                    || operations.ValueKind != JsonValueKind.Array)
                {
                    Die("producer HTTP conformance fixture has an invalid shape");
                }
            }
            else if (relative.EndsWith("reactive.json"))
            {
                if (!NumberEquals(value, "schemaVersion", 1)
                    || value.ValueKind != JsonValueKind.Object
                    || !value.TryGetProperty("attach", out var attach)
                    || !IsTruthy(attach))
                {
                    Die("producer reactive conformance fixture has an invalid shape");
                }
            }
            result[relative] = Sha256Hex(File.ReadAllBytes(path));
        }
        return result;
    }

    private static bool IsTruthy(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => element.GetString()!.Length > 0,
        JsonValueKind.Number => element.GetDouble() != 0,
        JsonValueKind.Array => element.GetArrayLength() > 0,
        JsonValueKind.Object => element.EnumerateObject().Any(),
        _ => false,
    };

    private static bool NumberEquals(JsonElement element, string key, double expected) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number
        && Math.Abs(value.GetDouble() - expected) < double.Epsilon;

    private static string? OptionalString(JsonElement element, string key)
    {
        string? result = null;
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            result = value.GetString();
        }
        return result;
    }

    // ---------- download + extraction ----------

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
            var chunk = new byte[64 * 1024];
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

    public static string UnpackUrl(string url, string archiveSha256, string destination,
        Func<string, byte[]>? download = null)
    {
        if (url.Length == 0)
        {
            Die("set CONTRACT_SOURCE or CONTRACT_URL with CONTRACT_ARCHIVE_SHA256");
        }
        if (archiveSha256.Length != 64 || !archiveSha256.All(char.IsAsciiHexDigit))
        {
            Die("CONTRACT_URL requires a 64-character CONTRACT_ARCHIVE_SHA256");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != "http" && parsed.Scheme != "https"))
        {
            Die("CONTRACT_URL must use http or https");
        }
        download ??= DownloadArchive;
        var payload = download(url);
        var actual = Sha256Hex(payload);
        if (!string.Equals(actual, archiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            Die($"contract archive SHA-256 mismatch: {actual} != {archiveSha256}");
        }
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
                    var normalized = SafeRelative(entry.Name, "archive entry name");
                    if (!seen.Add(normalized))
                    {
                        Die($"downloaded contract archive repeats entry: '{entry.Name}'");
                    }
                    var target = Path.GetFullPath(Path.Join(extracted, normalized));
                    if (!target.StartsWith(Path.GetFullPath(extracted) + Path.DirectorySeparatorChar))
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
                            Die("downloaded contract archive is too large uncompressed");
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
                    var normalized = SafeRelative(entry.Name, "archive entry name");
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

    public static string LoadProducer(string source, string url, string archiveSha256, string temporary,
        Func<string, byte[]>? download = null)
    {
        if (source.Length > 0 && url.Length > 0)
        {
            Die("set only one of CONTRACT_SOURCE or CONTRACT_URL");
        }
        if (source.Length > 0)
        {
            var producer = Path.GetFullPath(ExpandUser(source));
            if (IsSymlink(producer))
            {
                Die($"producer contract directory is a symlink: {producer}");
            }
            if (!Directory.Exists(producer))
            {
                Die($"producer contract directory is unavailable: {producer}");
            }
            RejectLinks(producer);
            return producer;
        }
        if (url.Length == 0)
        {
            Die("no contract source configured; set CONTRACT_SOURCE or immutable CONTRACT_URL");
        }
        return UnpackUrl(url, archiveSha256, temporary, download);
    }

    private static string ExpandUser(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    // ---------- Rust rendering ----------

    /// python json.dumps(value, ensure_ascii=False): quote/backslash/control
    /// escapes only; non-ASCII stays raw.
    public static string RustString(string value)
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
                    if (c < 0x20)
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

    private static readonly HashSet<string> RustKeywords =
    [
        "type", "self", "Self", "crate", "super", "mod", "ref", "move", "match", "loop",
        "const", "static", "enum", "struct", "fn", "where", "use", "pub", "impl", "trait",
        "async", "await", "dyn", "in", "as", "for", "return", "true", "false",
    ];

    public static string RustIdent(string value)
    {
        var result = NonIdentifierRegex().Replace(value, "_");
        if (result.Length == 0 || char.IsAsciiDigit(result[0]))
        {
            result = "_" + result;
        }
        if (RustKeywords.Contains(result))
        {
            result += "_";
        }
        return result;
    }

    /// Convert an OpenAPI property name to an idiomatic Rust field name.
    public static string RustFieldIdent(string value) =>
        RustIdent(CamelBoundaryRegex().Replace(value, "$1_$2").ToLowerInvariant());

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NonIdentifierRegex();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex CamelBoundaryRegex();

    public static string SchemaType(JsonElement schema, bool required, string? forcedType = null)
    {
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$ref", out var reference))
        {
            return RustIdent((reference.GetString() ?? "").Split('/')[^1]);
        }
        if (forcedType is null
            && schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.Array)
        {
            var nonNull = typeElement.EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .Where(item => item != "null").ToArray();
            if (nonNull.Length == 1)
            {
                return $"Option<{SchemaType(schema, required: true, forcedType: nonNull[0])}>";
            }
            return "serde_json::Value";
        }
        if (schema.ValueKind == JsonValueKind.Object
            && (schema.TryGetProperty("oneOf", out var options) || schema.TryGetProperty("anyOf", out options)))
        {
            if (options.ValueKind == JsonValueKind.Array)
            {
                var refs = options.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("$ref", out _))
                    .ToArray();
                if (refs.Length == 1)
                {
                    return SchemaType(refs[0], required);
                }
            }
            return "serde_json::Value";
        }
        string? kind = forcedType;
        if (kind is null && schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("type", out var kindElement))
        {
            if (kindElement.ValueKind == JsonValueKind.String)
            {
                kind = kindElement.GetString();
            }
            else if (kindElement.ValueKind == JsonValueKind.Array)
            {
                var nonNull = kindElement.EnumerateArray()
                    .Select(item => item.GetString() ?? "")
                    .Where(item => item != "null").ToArray();
                kind = nonNull.Length == 1 ? nonNull[0] : null;
            }
        }
        if (kind is null
            && schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out _))
        {
            kind = "object";
        }
        switch (kind)
        {
            case "string":
                return "String";
            case "integer":
                var format = OptionalString(schema, "format");
                return format switch
                {
                    "uint64" => "u64",
                    "uint32" => "u32",
                    "uint16" => "u16",
                    "uint8" => "u8",
                    "int32" => "i32",
                    "int16" => "i16",
                    "int8" => "i8",
                    _ => "i64",
                };
            case "number":
                return "f64";
            case "boolean":
                return "bool";
            case "array":
                var items = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("items", out var itemsElement)
                    ? itemsElement : default;
                return $"Vec<{SchemaType(items, required: true)}>";
            case "object":
                return "std::collections::BTreeMap<String, serde_json::Value>";
            default:
                return "serde_json::Value";
        }
    }

    /// python: schema_type(next(iter(content.values())).get("schema", {})) — the
    /// FIRST media type wins, missing schema degrades to serde_json::Value.
    private static string FirstMediaSchema(JsonElement content)
    {
        foreach (var media in content.EnumerateObject())
        {
            var schema = media.Value.ValueKind == JsonValueKind.Object
                && media.Value.TryGetProperty("schema", out var schemaElement)
                ? schemaElement : default;
            return SchemaType(schema, required: true);
        }
        return "";
    }

    public static string RenderGenerated(string root, JsonElement manifest, SortedDictionary<string, string> digests,
        SortedDictionary<string, string> corpus)
    {
        using var openapiDocument = LoadJson(Path.Join(root, "openapi.json"));
        using var msgpackDocument = LoadJson(Path.Join(root, "msgpack/terrascale.sync.v1.msgpack.json"));
        var openapi = openapiDocument.RootElement;
        var msgpack = msgpackDocument.RootElement;
        var models = openapi.ValueKind == JsonValueKind.Object
            && openapi.TryGetProperty("components", out var components)
            && components.ValueKind == JsonValueKind.Object
            && components.TryGetProperty("schemas", out var schemas)
            && schemas.ValueKind == JsonValueKind.Object
                ? schemas : default;
        var lines = new List<string>
        {
            "// Code generated by scripts/contracts.cs; DO NOT EDIT.",
            "// Source: the pinned terrabase contract bundle.",
            "#![allow(clippy::all)]",
            "use serde::{Deserialize, Serialize};",
            "",
            $"pub const CONTRACT_VERSION: &str = {RustString(manifest.GetProperty("contractVersion").GetString()!)};",
            $"pub const MANIFEST_SHA256: &str = {RustString(Sha256Hex(File.ReadAllBytes(Path.Join(root, "manifest.json"))))};",
            $"pub const OPENAPI_BLAKE3: &str = {RustString(digests["openapi.json"])};",
            $"pub const MSGPACK_MANIFEST_BLAKE3: &str = {RustString(digests["msgpack/terrascale.sync.v1.msgpack.json"])};",
            $"pub const MSGPACK_PROTOCOL: &str = {RustString(OptionalString(msgpack, "protocol") ?? "")};",
            $"pub const MSGPACK_PROTOCOL_MAJOR: u32 = {JsonNumber(msgpack, "protocolMajor")};",
            $"pub const MSGPACK_PROTOCOL_MINOR: u32 = {JsonNumber(msgpack, "protocolMinor")};",
            $"pub const MSGPACK_SUPPORTED_FEATURES: u32 = {JsonNumber(msgpack, "supportedFeatures")};",
            "",
            "#[derive(Debug, Clone, Copy, PartialEq, Eq)]",
            "pub struct ContractArtifact { pub path: &'static str, pub blake3: &'static str }",
            "",
            "pub const ARTIFACTS: &[ContractArtifact] = &[",
        };
        foreach (var (relative, digest) in digests)
        {
            lines.Add($"    ContractArtifact {{ path: {RustString(relative)}, blake3: {RustString(digest)} }},");
        }
        lines.AddRange(["];", "", "#[derive(Debug, Clone, Copy, PartialEq, Eq)]",
            "pub enum HttpMethod { Get, Post, Put, Patch, Delete, Options, Head, Trace }",
            "",
            "#[derive(Debug, Clone, Copy, PartialEq, Eq)]",
            "pub struct Route { pub operation_id: &'static str, pub method: HttpMethod, pub path: &'static str, pub request_model: Option<&'static str>, pub response_model: Option<&'static str> }",
            "pub type Operation = Route;",
            "",
        ]);
        var operations = new List<(string OperationId, string Path, string Method, JsonElement Operation)>();
        if (openapi.ValueKind == JsonValueKind.Object && openapi.TryGetProperty("paths", out var paths)
            && paths.ValueKind == JsonValueKind.Object)
        {
            foreach (var pathProperty in paths.EnumerateObject())
            {
                if (pathProperty.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (var methodProperty in pathProperty.Value.EnumerateObject())
                {
                    if (methodProperty.Name is not ("get" or "post" or "put" or "patch" or "delete" or "options" or "head" or "trace"))
                    {
                        continue;
                    }
                    var operationId = OptionalString(methodProperty.Value, "operationId") ?? "";
                    operations.Add((operationId, pathProperty.Name, methodProperty.Name, methodProperty.Value));
                }
            }
        }
        operations = [.. operations
            .OrderBy(entry => entry.OperationId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Method, StringComparer.Ordinal)];
        lines.Add("pub const ROUTES: &[Route] = &[");
        foreach (var (operationId, path, method, operation) in operations)
        {
            string? request = null;
            if (operation.TryGetProperty("requestBody", out var requestBody)
                && requestBody.ValueKind == JsonValueKind.Object
                && requestBody.TryGetProperty("content", out var requestContent)
                && requestContent.ValueKind == JsonValueKind.Object
                && requestContent.EnumerateObject().Any())
            {
                request = FirstMediaSchema(requestContent);
            }
            string? response = null;
            if (operation.TryGetProperty("responses", out var responses)
                && responses.ValueKind == JsonValueKind.Object)
            {
                foreach (var status in responses.EnumerateObject()
                             .OrderBy(property => StatusSortKey(property.Name)))
                {
                    if (!status.Name.StartsWith('2'))
                    {
                        continue;
                    }
                    if (status.Value.ValueKind == JsonValueKind.Object
                        && status.Value.TryGetProperty("content", out var responseContent)
                        && responseContent.ValueKind == JsonValueKind.Object
                        && responseContent.EnumerateObject().Any())
                    {
                        response = FirstMediaSchema(responseContent);
                    }
                    break;
                }
            }
            var methodName = char.ToUpperInvariant(method[0]) + method[1..];
            var requestExpr = request is not null ? $"Some({RustString(request)})" : "None";
            var responseExpr = response is not null ? $"Some({RustString(response)})" : "None";
            lines.Add(
                $"    Route {{ operation_id: {RustString(operationId)}, method: HttpMethod::{methodName}, path: {RustString(path)}, request_model: {requestExpr}, response_model: {responseExpr} }},");
        }
        lines.AddRange(["];", "pub const OPERATIONS: &[Operation] = ROUTES;", "", "pub mod models {", "    use super::*;", ""]);
        if (models.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in models.EnumerateObject().Select(property => property.Name).Order())
            {
                var schema = models.GetProperty(name);
                var ident = RustIdent(name);
                if (OptionalString(schema, "type") == "object"
                    && schema.TryGetProperty("properties", out var properties)
                    && properties.ValueKind == JsonValueKind.Object)
                {
                    var requiredSet = new HashSet<string>();
                    if (schema.TryGetProperty("required", out var requiredElement)
                        && requiredElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in requiredElement.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                requiredSet.Add(item.GetString()!);
                            }
                        }
                    }
                    lines.AddRange(["    #[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]", $"    pub struct {ident} {{"]);
                    foreach (var fieldProperty in properties.EnumerateObject())
                    {
                        var field = fieldProperty.Name;
                        var fieldIdent = RustFieldIdent(field);
                        var fieldType = SchemaType(fieldProperty.Value, requiredSet.Contains(field));
                        if (!requiredSet.Contains(field) && !fieldType.StartsWith("Option<", StringComparison.Ordinal))
                        {
                            fieldType = $"Option<{fieldType}>";
                        }
                        if (fieldIdent != field)
                        {
                            lines.Add($"        #[serde(rename = {RustString(field)})]");
                        }
                        lines.Add($"        pub {fieldIdent}: {fieldType},");
                    }
                    lines.AddRange(["    }", ""]);
                }
                else
                {
                    lines.AddRange([$"    pub type {ident} = serde_json::Value;", ""]);
                }
            }
        }
        lines.AddRange(["}", "", "#[derive(Debug, Clone, Copy, PartialEq, Eq)]", "pub struct MessagePackEnvelopeField { pub index: u8, pub name: &'static str, pub kind: &'static str }", ""]);
        lines.Add("pub const MSGPACK_ENVELOPE: &[MessagePackEnvelopeField] = &[");
        if (msgpack.ValueKind == JsonValueKind.Object
            && msgpack.TryGetProperty("envelope", out var envelope)
            && envelope.ValueKind == JsonValueKind.Object
            && envelope.TryGetProperty("elements", out var elements)
            && elements.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in elements.EnumerateArray())
            {
                var kind = "unknown";
                if (element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("type", out var typeElement)
                    && typeElement.ValueKind == JsonValueKind.Object
                    && typeElement.TryGetProperty("kind", out var kindElement)
                    && kindElement.ValueKind == JsonValueKind.String)
                {
                    kind = kindElement.GetString()!;
                }
                lines.Add(
                    $"    MessagePackEnvelopeField {{ index: {JsonNumber(element, "index")}, name: {RustString(OptionalString(element, "name") ?? "")}, kind: {RustString(kind)} }},");
            }
        }
        lines.AddRange(["];", "", "#[derive(Debug, Clone, Copy, PartialEq, Eq)]", "pub struct MessagePackMessage { pub name: &'static str, pub tag: u32, pub direction: &'static str }", "", "pub const MSGPACK_MESSAGES: &[MessagePackMessage] = &["]);
        if (msgpack.ValueKind == JsonValueKind.Object
            && msgpack.TryGetProperty("messages", out var messages)
            && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray()
                         .OrderBy(item => item.ValueKind == JsonValueKind.Object
                             && item.TryGetProperty("tag", out var tag)
                             && tag.TryGetInt64(out var tagValue) ? tagValue : 0))
            {
                lines.Add(
                    $"    MessagePackMessage {{ name: {RustString(OptionalString(message, "name") ?? "")}, tag: {JsonNumber(message, "tag")}, direction: {RustString(OptionalString(message, "direction") ?? "")} }},");
            }
        }
        lines.AddRange(["];", "", "#[derive(Debug, Clone, Copy, PartialEq, Eq)]", "pub struct MessagePackType { pub name: &'static str, pub kind: &'static str }", "", "pub const MSGPACK_TYPES: &[MessagePackType] = &["]);
        if (msgpack.ValueKind == JsonValueKind.Object
            && msgpack.TryGetProperty("types", out var types)
            && types.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in types.EnumerateArray())
            {
                JsonElement definition = default;
                var kind = "unknown";
                if (item.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in item.EnumerateObject())
                    {
                        definition = property.Value;
                        kind = property.Name;
                        break;
                    }
                }
                lines.Add(
                    $"    MessagePackType {{ name: {RustString(OptionalString(definition, "name") ?? "")}, kind: {RustString(kind)} }},");
            }
        }
        lines.AddRange(["];", "", "#[derive(Debug, Clone, Copy, PartialEq, Eq)]", "pub struct MessagePackScan { pub max_message_bytes: u32, pub max_depth: u32, pub max_string_bytes: u32, pub max_binary_bytes: u32, pub max_array_items: u32, pub max_map_entries: u32, pub extensions_allowed: bool, pub float32_allowed: bool }", "", "pub const MSGPACK_SCAN: MessagePackScan = MessagePackScan {"]);
        var scan = msgpack.ValueKind == JsonValueKind.Object
            && msgpack.TryGetProperty("scan", out var scanElement)
            && scanElement.ValueKind == JsonValueKind.Object
                ? scanElement : default;
        var scanFields = new (string Key, string Field, bool IsBool)[]
        {
            ("maxMessageBytes", "max_message_bytes", false),
            ("maxDepth", "max_depth", false),
            ("maxStringBytes", "max_string_bytes", false),
            ("maxBinaryBytes", "max_binary_bytes", false),
            ("maxArrayItems", "max_array_items", false),
            ("maxMapEntries", "max_map_entries", false),
            ("extensionsAllowed", "extensions_allowed", true),
            ("float32Allowed", "float32_allowed", true),
        };
        foreach (var (key, field, isBool) in scanFields)
        {
            var rendered = isBool ? "false" : "0";
            if (scan.ValueKind == JsonValueKind.Object && scan.TryGetProperty(key, out var value))
            {
                rendered = value.ValueKind switch
                {
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => value.ToString()!,
                    _ => rendered,
                };
            }
            lines.Add($"    {field}: {rendered},");
        }
        lines.AddRange(["};", "",
            $"pub const MESSAGE_VECTOR_SHA256: &str = {RustString(corpus["conformance/msgpack/message-vectors.json"])};",
            $"pub const HOSTILE_VECTOR_SHA256: &str = {RustString(corpus["conformance/msgpack/hostile-vectors.json"])};",
            $"pub const HTTP_CONFORMANCE_SHA256: &str = {RustString(corpus["conformance/http.json"])};",
            $"pub const REACTIVE_CONFORMANCE_SHA256: &str = {RustString(corpus["conformance/reactive.json"])};",
            ""]);
        return string.Join("\n", lines);
    }

    /// python: int(value) if str(value).isdigit() else 999 (ASCII digits only in practice).
    private static int StatusSortKey(string status) =>
        int.TryParse(status, out var parsed) ? parsed : 999;

    private static string JsonNumber(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Number)
        {
            return value.ToString()!;
        }
        return "0";
    }

    public static void Rustfmt(string path)
    {
        var start = new ProcessStartInfo("rustfmt")
        {
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--edition");
        start.ArgumentList.Add("2021");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Die($"rustfmt failed on {path}: {error.Trim()}");
        }
    }

    // ---------- install ----------

    public static void ScopedRemove(string path, string parent, string prefix)
    {
        var name = Path.GetFileName(path);
        var isSymlink = StatLink(path, out var info) == 0 && (info.Mode & SIfMt) == SIfLnk;
        if (Path.GetDirectoryName(path) != parent || !name.StartsWith(prefix) || isSymlink)
        {
            Die($"refusing to remove unresolved path: {path}");
        }
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
