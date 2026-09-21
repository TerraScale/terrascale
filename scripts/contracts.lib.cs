// Install and verify the pinned TerraScale contract bundle.
// Port of the former scripts/contracts.py — same caps, same fail-closed rules,
// same messages, byte-identical provenance.json.
//
// The producer (terrabase) is authoritative. A consumer may use a producer
// directory only when CONTRACT_SOURCE is explicitly supplied, or may download
// an immutable archive with CONTRACT_URL and CONTRACT_ARCHIVE_SHA256. Nothing
// in this module guesses a sibling checkout.

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
    public const int MaxArchiveBytes = 32 * 1024 * 1024;
    public const int MaxArchiveEntryBytes = 8 * 1024 * 1024;
    public const int MaxArchiveTotalBytes = 32 * 1024 * 1024;
    public const int MaxArchiveMembers = 4096;
    public const int MaxCorpusBytes = 8 * 1024 * 1024;

    public static readonly string[] RequiredArtifacts =
    [
        "openapi.json",
        "msgpack/terrascale.sync.v1.msgpack.json",
        "proto/terrabase/v1/engine.proto",
        "proto/terrabase/v1/family.proto",
    ];

    // Conformance is published by the producer at the same seam as the contract.
    // Keep it in the installed bundle even while older draft manifests only list
    // the four protocol artifacts; a consumer must never silently reuse a stale
    // local corpus.
    public static readonly string[] CorpusFiles =
    [
        "conformance/http.json",
        "conformance/origin-proof.json",
        "conformance/reactive.json",
        "conformance/msgpack/message-vectors.json",
        "conformance/msgpack/hostile-vectors.json",
    ];

    [DoesNotReturn]
    public static void Fail(string message) => throw new ContractError($"contracts: {message}");

    private static bool IsVersion(string value) =>
        value.Length >= 2 && value[0] == 'v' && value[1] >= '1' && value[1] <= '9'
        && value[1..].All(char.IsAsciiDigit);

    private static bool IsHex64(string value) =>
        value.Length == 64 && value.All(c => char.IsAsciiHexDigit(c));

    private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // ---------- filesystem guards (lstat semantics: symlink + nlink == 1) ----------

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
    private static extern int Stat(string path, out SystemStat buffer);

    private const uint SIfMt = 0xF000;
    private const uint SIfReg = 0x8000;
    private const uint SIfLnk = 0xA000;
    private const uint SIfDir = 0x4000;

    private static SystemStat LStat(string path, string label)
    {
        if (Stat(path, out var info) != 0)
        {
            Fail($"{label} is unavailable: errno {Marshal.GetLastWin32Error()}");
        }
        return info;
    }

    public static void RegularFile(string path, string label)
    {
        var info = LStat(path, label);
        if ((info.Mode & SIfMt) == SIfLnk)
        {
            Fail($"{label} is a symlink");
        }
        if ((info.Mode & SIfMt) != SIfReg)
        {
            Fail($"{label} is not a regular file");
        }
        if (info.NLink != 1)
        {
            Fail($"{label} is a hard link");
        }
    }

    public static void RejectLinksAndSpecials(string root)
    {
        var rootInfo = LStat(root, root);
        if ((rootInfo.Mode & SIfMt) == SIfLnk)
        {
            Fail($"contract root is a symlink: {root}");
        }
        if ((rootInfo.Mode & SIfMt) != SIfDir)
        {
            Fail($"contract root is not a directory: {root}");
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var info = LStat(path, path);
            if ((info.Mode & SIfMt) == SIfLnk)
            {
                Fail($"contract input contains a symlink: {path}");
            }
            if ((info.Mode & SIfMt) == SIfReg)
            {
                if (info.NLink != 1)
                {
                    Fail($"contract input contains a hard link: {path}");
                }
            }
            else if ((info.Mode & SIfMt) != SIfDir)
            {
                Fail($"contract input contains a special entry: {path}");
            }
        }
    }

    // ---------- JSON (document order preserved, duplicate keys rejected) ----------

    public static JsonDocument ReadJson(string path)
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
            Fail($"invalid JSON in {path}: {error.Message}");
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
                        Fail($"duplicate JSON key '{property.Name}' in {path}");
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

    private static string RequireString(JsonElement element, string key)
    {
        string? result = null;
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            result = value.GetString();
        }
        if (result is null)
        {
            Fail($"expected string field '{key}'");
        }
        return result;
    }

    private static bool NumberEquals(JsonElement element, string key, double expected) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number
        && Math.Abs(value.GetDouble() - expected) < double.Epsilon;

    private static string? OptionalString(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ---------- paths ----------

    private static string ExpandUser(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    public static string SafeRelative(string value, string label)
    {
        if (value.Length == 0 || value.Contains('\0') || value.Contains('\\'))
        {
            Fail($"{label} has an invalid path");
        }
        if (value.StartsWith('/'))
        {
            Fail($"{label} contains path traversal: '{value}'");
        }
        var parts = new List<string>();
        foreach (var part in value.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                Fail($"{label} contains path traversal: '{value}'");
            }
            parts.Add(part);
        }
        var normalized = string.Join("/", parts);
        if (normalized.Length == 0 || normalized == "." || normalized != value.TrimEnd('/'))
        {
            Fail($"{label} is not normalized: '{value}'");
        }
        return normalized;
    }

    // ---------- bundles ----------

    public static (JsonElement Manifest, SortedDictionary<string, string> Digests) ValidateBundle(string root, string version)
    {
        if (!IsVersion(version))
        {
            Fail($"invalid contract version '{version}'");
        }
        RejectLinksAndSpecials(root);
        using var manifestDocument = ReadJson(Path.Join(root, "manifest.json"));
        var manifest = manifestDocument.RootElement;
        if (!NumberEquals(manifest, "schemaVersion", 1))
        {
            Fail("manifest schemaVersion is not 1");
        }
        if (RequireString(manifest, "contractVersion") != version)
        {
            Fail($"manifest contractVersion is '{OptionalString(manifest, "contractVersion")}', expected '{version}'");
        }
        if (!manifest.TryGetProperty("artifacts", out var artifacts)
            || artifacts.ValueKind != JsonValueKind.Array
            || artifacts.GetArrayLength() == 0)
        {
            Fail("manifest has no artifacts");
        }
        var digests = new SortedDictionary<string, string>();
        foreach (var entry in artifacts.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                Fail("manifest artifact is not an object");
            }
            var relative = RequireString(entry, "path");
            var expected = RequireString(entry, "blake3");
            if (!IsHex64(expected))
            {
                Fail($"manifest artifact has an invalid path or digest: '{relative}'");
            }
            relative = SafeRelative(relative, "manifest artifact path");
            if (digests.ContainsKey(relative))
            {
                Fail($"manifest repeats artifact path: {relative}");
            }
            var artifactPath = Path.Join(root, relative);
            RegularFile(artifactPath, relative);
            var actual = Blake3Hex(File.ReadAllBytes(artifactPath));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                Fail($"manifest digest mismatch for {relative}: {actual} != {expected}");
            }
            digests[relative] = expected.ToLowerInvariant();
        }
        foreach (var relative in RequiredArtifacts)
        {
            if (!digests.ContainsKey(relative))
            {
                Fail($"manifest does not pin required artifact {relative}");
            }
        }
        return (manifest.Clone(), digests);
    }

    public static string Blake3Hex(byte[] data)
    {
        using var hasher = Blake3.Hasher.New();
        hasher.Update(data);
        return hasher.Finalize().ToString();
    }

    public static SortedDictionary<string, string> ValidateCorpus(string root, string? version = null)
    {
        var result = new SortedDictionary<string, string>();
        var manifestPath = Path.Join(root, "msgpack/terrascale.sync.v1.msgpack.json");
        RegularFile(manifestPath, "MessagePack manifest");
        var manifestSha = Sha256Hex(File.ReadAllBytes(manifestPath));
        foreach (var relative in CorpusFiles)
        {
            var path = CorpusFile(root, relative);
            RegularFile(path, relative);
            if (new FileInfo(path).Length > MaxCorpusBytes)
            {
                Fail($"corpus file is too large: {relative}");
            }
            using var document = ReadJson(path);
            var value = document.RootElement;
            if (version is not null && RequireString(value, "contractVersion") != version)
            {
                Fail($"corpus {relative} pins a different contract version");
            }
            if (relative.EndsWith("http.json"))
            {
                if (!NumberEquals(value, "schemaVersion", 1)
                    || !value.TryGetProperty("operations", out var operations)
                    || operations.ValueKind != JsonValueKind.Array
                    || operations.GetArrayLength() == 0)
                {
                    Fail("HTTP conformance fixture is incomplete");
                }
            }
            else if (relative.EndsWith("origin-proof.json"))
            {
                foreach (var key in (string[])
                         ["contractVersion", "method", "target", "bucket", "now", "region", "secret", "proof"])
                {
                    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out _))
                    {
                        Fail("origin proof fixture is incomplete");
                    }
                }
                if (RequireString(value, "contractVersion") != version)
                {
                    Fail("origin proof fixture pins a different contract version");
                }
            }
            else if (relative.EndsWith("reactive.json"))
            {
                if (!NumberEquals(value, "schemaVersion", 1)
                    || !value.TryGetProperty("attach", out var attach)
                    || attach.ValueKind != JsonValueKind.Object)
                {
                    Fail("reactive conformance fixture is incomplete");
                }
            }
            else
            {
                if (!value.TryGetProperty("vectors", out var vectors)
                    || vectors.ValueKind != JsonValueKind.Array)
                {
                    Fail($"corpus {relative} has no vector list");
                }
                if (relative.EndsWith("message-vectors.json"))
                {
                    if (RequireString(value, "protocol") != "terrascale.sync.v1.msgpack"
                        || RequireString(value, "version") != "1.0")
                    {
                        Fail("MessagePack corpus identity is not 1.0");
                    }
                    if (RequireString(value, "manifestSha256") != manifestSha)
                    {
                        Fail("MessagePack corpus pins a different manifest SHA-256");
                    }
                    if (vectors.GetArrayLength() < 11)
                    {
                        Fail("MessagePack corpus is incomplete");
                    }
                    foreach (var vector in vectors.EnumerateArray())
                    {
                        var hex = OptionalString(vector, "hex");
                        if (vector.ValueKind != JsonValueKind.Object || hex is null)
                        {
                            Fail("MessagePack corpus contains an invalid vector");
                        }
                        var wire = TryFromHex(hex);
                        if (wire is null)
                        {
                            Fail("MessagePack corpus contains non-hex wire data");
                        }
                        if (wire.Length == 0 || wire.Length > MaxCorpusBytes)
                        {
                            Fail("MessagePack corpus contains an oversized wire vector");
                        }
                    }
                }
                else
                {
                    if (vectors.GetArrayLength() < 10)
                    {
                        Fail("hostile MessagePack corpus is incomplete");
                    }
                    foreach (var vector in vectors.EnumerateArray())
                    {
                        if (vector.ValueKind != JsonValueKind.Object)
                        {
                            Fail("hostile corpus contains an invalid vector");
                        }
                        if (vector.TryGetProperty("repeat", out var repeat))
                        {
                            if (repeat.ValueKind != JsonValueKind.Object
                                || !repeat.TryGetProperty("count", out var count)
                                || !count.TryGetInt64(out var repeatCount)
                                || repeatCount < 0
                                || repeatCount > MaxCorpusBytes)
                            {
                                Fail("hostile corpus repeat is outside the bound");
                            }
                        }
                        else if (OptionalString(vector, "hex") is { } hex)
                        {
                            if (TryFromHex(hex) is null)
                            {
                                Fail("hostile corpus contains non-hex wire data");
                            }
                        }
                        else
                        {
                            Fail("hostile corpus contains an invalid vector");
                        }
                    }
                }
            }
            result[relative] = Sha256Hex(File.ReadAllBytes(path));
        }
        return result;
    }

    private static byte[]? TryFromHex(string hex)
    {
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ---------- archives ----------

    public static string ExtractArchiveRoot(byte[] payload, string destination)
    {
        if (payload.Length > MaxArchiveBytes)
        {
            Fail("contract archive is too large");
        }
        var extracted = Path.Join(destination, "extracted");
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
                        Fail("contract archive has too many entries");
                    }
                    var normalized = SafeRelative(entry.Name, "archive entry name");
                    if (!seen.Add(normalized))
                    {
                        Fail($"contract archive repeats entry: '{entry.Name}'");
                    }
                    var target = Path.GetFullPath(Path.Join(extracted, normalized));
                    if (!target.StartsWith(Path.GetFullPath(extracted) + Path.DirectorySeparatorChar))
                    {
                        Fail("contract archive contains an unsafe path");
                    }
                    if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                    {
                        Fail($"contract archive contains a link: '{entry.Name}'");
                    }
                    if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile))
                    {
                        Fail($"contract archive contains a special entry: '{entry.Name}'");
                    }
                    if (entry.EntryType == TarEntryType.RegularFile)
                    {
                        if (entry.Length < 0 || entry.Length > MaxArchiveEntryBytes)
                        {
                            Fail($"contract archive entry is too large: '{entry.Name}'");
                        }
                        total += entry.Length;
                        if (total > MaxArchiveTotalBytes)
                        {
                            Fail("contract archive uncompressed content is too large");
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
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        {
            Fail($"contract archive is invalid: {error.Message}");
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
        Fail("contract archive has no manifest.json");
        throw new ContractError("unreachable");
    }

    public static byte[] DownloadArchive(string url)
    {
        try
        {
            // Cloudflare rejects unknown user agents with 403.
            using var client = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(30),
            });
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
                    Fail("downloaded contract archive is too large");
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
            Fail($"could not download CONTRACT_URL: {error.Message}");
            throw;
        }
    }

    public static string LoadProducer(string source, string url, string archiveSha256, string temporary,
        Func<string, byte[]>? download = null)
    {
        if (source.Length > 0 && url.Length > 0)
        {
            Fail("set only one of CONTRACT_SOURCE or CONTRACT_URL");
        }
        if (source.Length > 0)
        {
            var producer = Path.GetFullPath(ExpandUser(source));
            RejectLinksAndSpecials(producer);
            return producer;
        }
        if (url.Length == 0)
        {
            Fail("set explicit CONTRACT_SOURCE or CONTRACT_URL; refusing sibling checkout inference");
        }
        if (!IsHex64(archiveSha256))
        {
            Fail("CONTRACT_URL requires a 64-character CONTRACT_ARCHIVE_SHA256");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != "http" && parsed.Scheme != "https"))
        {
            Fail("CONTRACT_URL must use http or https");
        }
        download ??= DownloadArchive;
        var payload = download(url);
        var actual = Sha256Hex(payload);
        if (!string.Equals(actual, archiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            Fail($"contract archive SHA-256 mismatch: {actual} != {archiveSha256}");
        }
        return ExtractArchiveRoot(payload, temporary);
    }

    public static string FindCorpus(string source, string explicitCorpus, string? existing)
    {
        var candidates = new List<string>();
        if (explicitCorpus.Length > 0)
        {
            candidates.Add(Environment.ExpandEnvironmentVariables(explicitCorpus));
        }
        // A pre-publication terrabase checkout keeps the generated conformance
        // directory beside its contracts directory. This is considered only after
        // an explicit source has been supplied; no sibling inference is used to
        // select a contract bundle.
        candidates.Add(source);
        candidates.Add(Path.GetDirectoryName(source.TrimEnd('/')) ?? ".");
        candidates.Add(Path.Join(Path.GetDirectoryName(source.TrimEnd('/')) ?? ".", "conformance"));
        if (existing is not null)
        {
            candidates.Add(existing);
        }
        var seen = new HashSet<string>();
        foreach (var candidate in candidates)
        {
            var absolute = Path.GetFullPath(candidate);
            if (!seen.Add(absolute))
            {
                continue;
            }
            // Accept either a contract root (conformance/...) or the conformance
            // directory itself (http.json, reactive.json, msgpack/...).
            if (CorpusFiles.All(relative => File.Exists(CorpusFile(absolute, relative))))
            {
                RejectLinksAndSpecials(absolute);
                return absolute;
            }
        }
        Fail("MessagePack corpus is missing; pass explicit CONTRACT_CORPUS_SOURCE");
        throw new ContractError("unreachable");
    }

    public static string CorpusFile(string root, string relative)
    {
        var candidates = new List<string> { Path.Join(root, relative) };
        var trimmed = relative.StartsWith("conformance/") ? relative["conformance/".Length..] : relative;
        candidates.Add(Path.Join(root, trimmed));
        if (relative.StartsWith("conformance/msgpack/"))
        {
            candidates.Add(Path.Join(root, "msgpack", Path.GetFileName(relative)));
        }
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return candidates[0];
    }

    // ---------- provenance (byte-identical to the python json.dumps output) ----------

    public static void WriteProvenance(string root, JsonElement manifest,
        SortedDictionary<string, string> digests, SortedDictionary<string, string> corpus)
    {
        var values = new SortedDictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["contractVersion"] = manifest.GetProperty("contractVersion").GetString(),
            ["manifestSha256"] = Sha256Hex(File.ReadAllBytes(Path.Join(root, "manifest.json"))),
        };
        var artifacts = new SortedDictionary<string, object?>();
        foreach (var (relative, digest) in digests)
        {
            artifacts[relative] = new SortedDictionary<string, object?>
            {
                ["blake3"] = digest,
                ["sha256"] = Sha256Hex(File.ReadAllBytes(Path.Join(root, relative))),
                ["bytes"] = new FileInfo(Path.Join(root, relative)).Length,
            };
        }
        values["artifacts"] = artifacts;
        var corpusNode = new SortedDictionary<string, object?>();
        foreach (var (relative, digest) in corpus)
        {
            corpusNode[relative] = new SortedDictionary<string, object?>
            {
                ["sha256"] = digest,
                ["bytes"] = new FileInfo(CorpusFile(root, relative)).Length,
            };
        }
        values["corpus"] = corpusNode;
        var json = SerializePythonStyle(values) + "\n";
        File.WriteAllText(Path.Join(root, "provenance.json"), json, new UTF8Encoding(false));
    }

    private static string SerializePythonStyle(object? value) => Serialize(value, 0);

    private static string Serialize(object? value, int depth)
    {
        var indent = new string(' ', 2 * depth);
        var inner = new string(' ', 2 * (depth + 1));
        switch (value)
        {
            case SortedDictionary<string, object?> dictionary:
                if (dictionary.Count == 0)
                {
                    return "{}";
                }
                var entries = dictionary.Select(pair =>
                    $"{inner}{JsonEscape(pair.Key)}: {Serialize(pair.Value, depth + 1)}");
                return "{\n" + string.Join(",\n", entries) + "\n" + indent + "}";
            case int or long:
                return value.ToString()!;
            case string text:
                return JsonEscape(text);
            default:
                Fail($"unserializable provenance value: {value?.GetType()}");
                throw new ContractError("unreachable");
        }
    }

    /// python json.dumps(ensure_ascii=True) string escaping, byte for byte.</summary>
    private static string JsonEscape(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text)
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

    // ---------- install ----------

    private static void ScopedRemove(string path, string parent, string prefix)
    {
        var name = Path.GetFileName(path);
        var isSymlink = Stat(path, out var info) == 0 && (info.Mode & SIfMt) == SIfLnk;
        if (Path.GetDirectoryName(path) != parent || !name.StartsWith(prefix) || isSymlink)
        {
            Fail($"refusing to remove unresolved path: {path}");
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

    public static void Install(string version, string repoRoot, string source = "", string url = "",
        string archiveSha256 = "", string corpusSource = "", string? destination = null,
        Func<string, byte[]>? download = null)
    {
        if (!IsVersion(version))
        {
            Fail($"invalid contract version '{version}'");
        }
        var repo = repoRoot;
        var contractsRoot = Path.Join(repo, "contracts");
        if (Stat(contractsRoot, out var rootInfo) == 0 && (rootInfo.Mode & SIfMt) == SIfLnk)
        {
            Fail("contracts directory is a symlink");
        }
        Directory.CreateDirectory(contractsRoot);
        var target = destination ?? Path.Join(contractsRoot, version);
        if (Path.GetDirectoryName(target) != contractsRoot)
        {
            Fail("contract destination must be the scoped version directory");
        }
        if (Stat(target, out var targetInfo) == 0
            && ((targetInfo.Mode & SIfMt) == SIfLnk || (targetInfo.Mode & SIfMt) != SIfDir))
        {
            Fail("contract destination is not a directory");
        }
        var temporary = Path.Combine(Path.GetTempPath(), $"terrascale-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            var producer = LoadProducer(source, url, archiveSha256, temporary, download);
            ValidateBundle(producer, version);
            var old = Directory.Exists(target) ? target : null;
            var corpus = FindCorpus(producer, corpusSource, old);
            var staging = Path.Join(contractsRoot, $".{version}.staging-{Guid.NewGuid():N}");
            string? backup = null;
            var installed = false;
            try
            {
                CopyTree(producer, staging);
                Directory.CreateDirectory(Path.Join(staging, "conformance/msgpack"));
                foreach (var relative in CorpusFiles)
                {
                    var destinationFile = Path.Join(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                    File.Copy(CorpusFile(corpus, relative), destinationFile, overwrite: true);
                }
                RejectLinksAndSpecials(staging);
                var (manifest, digests) = ValidateBundle(staging, version);
                var corpusDigests = ValidateCorpus(staging, version);
                WriteProvenance(staging, manifest, digests, corpusDigests);
                if (Directory.Exists(target))
                {
                    backup = Path.Join(contractsRoot, $".{version}.backup-{Guid.NewGuid():N}");
                    Directory.Move(target, backup);
                }
                Directory.Move(staging, target);
                installed = true;
                if (backup is not null)
                {
                    ScopedRemove(backup, contractsRoot, $".{version}.backup-");
                }
                Console.WriteLine($"contracts: installed {version} from explicit producer input");
            }
            catch
            {
                if (Directory.Exists(staging))
                {
                    ScopedRemove(staging, contractsRoot, $".{version}.staging-");
                }
                if (installed)
                {
                    var failed = Path.Join(contractsRoot, $".{version}.failed-{Guid.NewGuid():N}");
                    Directory.Move(target, failed);
                    if (backup is not null && Directory.Exists(backup))
                    {
                        Directory.Move(backup, target);
                    }
                    else
                    {
                        ScopedRemove(failed, contractsRoot, $".{version}.failed-");
                    }
                }
                else if (backup is not null && Directory.Exists(backup) && !Directory.Exists(target))
                {
                    Directory.Move(backup, target);
                }
                throw;
            }
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Join(to, Path.GetRelativePath(from, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Join(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}

