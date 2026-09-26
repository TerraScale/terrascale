using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Buffers.Binary;

namespace TerraBase.Benchmarks;

/// <summary>Reads assembly metadata embedded by the benchmark project files.</summary>
public static class BenchmarkAssemblyMetadata
{
    /// <summary>Returns the metadata value for <paramref name="key"/>, or null when absent or empty.</summary>
    public static string? Value(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value is { Length: > 0 } value
            ? value
            : null;
    }
}

/// <summary>
/// Compact run-level receipt emitted by every benchmark invocation: subject
/// provenance (repo, commit, tree digest, dirty state), toolchain and machine
/// identity, UTC timestamps, the run result, and a SHA-256 digest over the
/// receipt payload (the digest field itself excluded). Follows the workspace
/// attestation convention: numeric schemaVersion plus lowercase hex digests.
/// </summary>
public static class RunReceipt
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "run-receipt.json";

    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Captures the provenance of the TerraBase subject this runner was built
    /// against. A frozen launch supplies an attested source identity through
    /// environment variables. In that mode the receipt's primary subject is
    /// the frozen identity and <see cref="SubjectProvenance.Live"/> preserves
    /// the checkout observation made by the process for drift diagnostics.
    /// </summary>
    /// <param name="subjectProject">Overrides the embedded subject project path (used by the self-check).</param>
    /// <exception cref="InvalidOperationException">The subject path is unknown, missing, or not a git checkout; the message names the failing path.</exception>
    public static SubjectProvenance CaptureSubject(string? subjectProject = null)
    {
        var project = subjectProject ?? BenchmarkAssemblyMetadata.Value("TerraBaseSubjectProject");
        if (string.IsNullOrWhiteSpace(project))
        {
            throw new InvalidOperationException(
                "Cannot resolve the TerraBase subject project: the build did not embed 'TerraBaseSubjectProject' assembly metadata. Build through the benchmark project files so the subject is recorded.");
        }

        if (!File.Exists(project))
        {
            throw new InvalidOperationException(
                $"TerraBase subject project not found at '{project}'. The benchmark measures the sibling terrabase checkout; restore it at that path or rebuild with /p:TerraBaseRepo=<path>.");
        }

        var projectDirectory = Path.GetFullPath(Path.GetDirectoryName(project)!);
        var repository = Git(projectDirectory, "rev-parse --show-toplevel");
        var live = CaptureLiveSubject(project, repository);
        return ApplyFrozenIdentity(live);
    }

    private static SubjectProvenance CaptureLiveSubject(string project, string repository)
    {
        var workingTreeManifestBytes = GitBytes(repository, "status --porcelain=v1 --untracked-files=all");
        var workingTreeManifest = Encoding.UTF8.GetString(workingTreeManifestBytes).TrimEnd('\r', '\n');
        var workingTreeDigest = WorkingTreeDigest(repository, workingTreeManifestBytes);
        return new SubjectProvenance(
            Project: Path.GetFullPath(project),
            Repository: repository,
            Commit: Git(repository, "rev-parse HEAD"),
            TreeDigest: Git(repository, "rev-parse HEAD^{tree}"),
            Dirty: workingTreeManifest.Length != 0,
            WorkingTreeDigest: workingTreeDigest,
            WorkingTreeChanges: workingTreeManifest.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    private static SubjectProvenance ApplyFrozenIdentity(SubjectProvenance live)
    {
        var frozen = FrozenSourceIdentity.ReadFromEnvironment();
        if (frozen is null)
        {
            return live;
        }

        var assemblyPath = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
        var closure = Path.GetFullPath(frozen.BinaryClosure);
        if (!IsPathWithin(assemblyPath, closure))
        {
            throw new InvalidOperationException(
                $"Frozen benchmark assembly '{assemblyPath}' is outside the declared binary closure '{closure}'.");
        }

        var assemblyDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant();
        if (!string.Equals(assemblyDigest, frozen.BenchmarkAssemblySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Frozen benchmark assembly digest does not match the launch identity: expected {frozen.BenchmarkAssemblySha256}, actual {assemblyDigest}.");
        }

        if (!File.Exists(frozen.ManifestPath) || File.Exists(frozen.ManifestPath) &&
            !string.Equals(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(frozen.ManifestPath))).ToLowerInvariant(),
                frozen.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Frozen source manifest '{frozen.ManifestPath}' is missing or its digest changed after launch.");
        }

        return new SubjectProvenance(
            Project: frozen.Project,
            Repository: frozen.Repository,
            Commit: frozen.Commit,
            TreeDigest: frozen.TreeDigest,
            Dirty: frozen.Dirty,
            WorkingTreeDigest: frozen.WorkingTreeDigest,
            WorkingTreeChanges: null,
            Live: live,
            Frozen: frozen);
    }

    private static bool IsPathWithin(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative is not (".." or "") && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || string.Equals(candidate, root, StringComparison.Ordinal);
    }

    /// <summary>Writes <c>run-receipt.json</c> into <paramref name="outputDirectory"/> and prints its path and digest.</summary>
    public static string Write(
        string outputDirectory,
        string workload,
        string status,
        int exitCode,
        DateTimeOffset startedAtUtc,
        DateTimeOffset finishedAtUtc,
        SubjectProvenance? subject,
        string? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        // The detailed receipts are written by the workload before this runs;
        // record them with digests so the compact receipt indexes its evidence.
        var artifacts = Directory.EnumerateFiles(outputDirectory, "*receipt.json", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFileName(path), FileName, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new ReceiptArtifact(
                File: Path.GetFileName(path),
                Bytes: new FileInfo(path).Length,
                Sha256: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()))
            .ToList();

        var payload = new RunReceiptPayload(
            SchemaVersion: CurrentSchemaVersion,
            Workload: workload,
            Status: status,
            StartedAtUtc: startedAtUtc.UtcDateTime,
            FinishedAtUtc: finishedAtUtc.UtcDateTime,
            ExitCode: exitCode,
            Command: Environment.CommandLine,
            OutputDirectory: Path.GetFullPath(outputDirectory),
            Subject: subject,
            Toolchain: ToolchainInfo.Capture(),
            Artifacts: artifacts,
            Error: error);

        var node = JsonSerializer.SerializeToNode(payload, CanonicalJson)!;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString(CanonicalJson)))).ToLowerInvariant();
        node["receiptSha256"] = digest;
        var receiptPath = Path.Combine(outputDirectory, FileName);
        File.WriteAllText(receiptPath, node.ToJsonString(PrettyJson) + "\n");

        Console.WriteLine($"Run receipt: {receiptPath}");
        Console.WriteLine($"Run receipt digest: sha256:{digest}");
        return receiptPath;
    }

    /// <summary>Recomputes the digest of a written run receipt and compares it to the recorded one.</summary>
    public static bool Verify(string receiptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        if (!File.Exists(receiptPath))
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(receiptPath));
            if (node is not JsonObject document ||
                document.Remove("receiptSha256", out var recorded) is false ||
                recorded is not JsonValue recordedValue ||
                !recordedValue.TryGetValue<string>(out var recordedDigest))
            {
                return false;
            }

            var computed = ComputeDigest(receiptPath);
            return computed is not null && string.Equals(recordedDigest, computed, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Computes the digest over a receipt's canonical JSON payload, excluding
    /// the recorded <c>receiptSha256</c> property. This is kept beside
    /// <see cref="Verify"/> so external qualification gates use the same
    /// System.Text.Json canonicalization as the writer.
    /// </summary>
    internal static string? ComputeDigest(string receiptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        if (!File.Exists(receiptPath))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(receiptPath));
            if (node is not JsonObject document || !document.Remove("receiptSha256"))
            {
                return null;
            }

            return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(document.ToJsonString(CanonicalJson)))).ToLowerInvariant();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>True when the process was invoked with the self-check mode.</summary>
    public static bool IsSelfCheckRequested(string[] args) =>
        args.Any(static argument => argument.Equals("--self-check", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Assert-style checks for the receipt logic: digest round-trip, tamper
    /// detection (payload and digest), loud failure naming a missing subject
    /// path, and a well-formed live subject capture. Returns the process exit
    /// code (0 = all checks passed).
    /// </summary>
    public static int SelfCheck()
    {
        var failures = 0;
        var root = Path.Combine(Path.GetTempPath(), $"terrabase-run-receipt-self-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var startedAtUtc = DateTimeOffset.UtcNow;
            Check("query-independent percentiles use the nearest-rank definition", BenchmarkPercentile.SelfCheck(), ref failures);
            var rawArtifactPath = Path.Combine(root, "query-independent-indexing-receipt.json");
            const string rawArtifactContents = "{\"schemaVersion\":1,\"rows\":[]}";
            File.WriteAllText(rawArtifactPath, rawArtifactContents);
            var subject = new SubjectProvenance("/self/check/TerraBase.csproj", "/self/check", new string('a', 40), new string('b', 40), Dirty: true);
            var receiptPath = Write(
                root, "self-check", "complete", 0, startedAtUtc, startedAtUtc.AddSeconds(1), subject);
            Check("receipt verifies after write", Verify(receiptPath), ref failures);
            var receiptNode = JsonNode.Parse(File.ReadAllText(receiptPath));
            var expectedArtifactSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawArtifactContents))).ToLowerInvariant();
            var artifactIndexed = receiptNode?["artifacts"]?.AsArray().Any(artifact =>
                artifact is JsonObject entry
                && entry["file"]?.GetValue<string>() == Path.GetFileName(rawArtifactPath)
                && entry["sha256"]?.GetValue<string>() == expectedArtifactSha256
                && entry["bytes"]?.GetValue<long>() == Encoding.UTF8.GetByteCount(rawArtifactContents)) == true;
            Check("raw query receipt is indexed by filename, byte length, and SHA-256", artifactIndexed, ref failures);

            var tamperedPayload = Path.Combine(root, "tampered-payload.json");
            File.WriteAllText(tamperedPayload, File.ReadAllText(receiptPath).Replace("self-check", "self-checx", StringComparison.Ordinal));
            Check("tampered payload is rejected", !Verify(tamperedPayload), ref failures);

            var tamperedDigest = Path.Combine(root, "tampered-digest.json");
            File.WriteAllText(tamperedDigest, File.ReadAllText(receiptPath).Replace("receiptSha256", "receiptSha257", StringComparison.Ordinal));
            Check("tampered digest field is rejected", !Verify(tamperedDigest), ref failures);

            var missingSubject = Path.Combine(Path.GetTempPath(), "definitely-missing-subject", "TerraBase.csproj");
            try
            {
                CaptureSubject(missingSubject);
                Check("missing subject fails loudly", success: false, ref failures);
            }
            catch (InvalidOperationException exception)
            {
                Check($"missing subject fails loudly and names the path ({exception.Message})", exception.Message.Contains(missingSubject, StringComparison.Ordinal), ref failures);
            }

            var gitRoot = Path.Combine(root, "fingerprint-git");
            Directory.CreateDirectory(gitRoot);
            var gitProject = Path.Combine(gitRoot, "TerraBase.csproj");
            File.WriteAllText(gitProject, "<Project />\n");
            Git(gitRoot, "init -q");
            Git(gitRoot, "add TerraBase.csproj");
            Git(gitRoot, "-c user.name=ReceiptSelfCheck -c user.email=receipt-self-check@example.invalid commit -qm baseline");
            var baseline = CaptureSubject(gitProject).WorkingTreeDigest;
            File.WriteAllText(gitProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            var changed = CaptureSubject(gitProject).WorkingTreeDigest;
            Check("tracked content changes working-tree digest", baseline is not null && changed is not null
                && !string.Equals(baseline, changed, StringComparison.Ordinal), ref failures);

            var manifestPath = Path.Combine(root, "frozen-manifest.txt");
            File.WriteAllText(manifestPath, "schema=receipt-self-check\n");
            var assemblyPath = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            var assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
            var frozenEnvironment = new Dictionary<string, string?>
            {
                ["TERRABASE_FROZEN_SOURCE_PROJECT"] = Path.GetFullPath(gitProject),
                ["TERRABASE_FROZEN_SOURCE_REPOSITORY"] = Path.GetFullPath(gitRoot),
                ["TERRABASE_FROZEN_SOURCE_COMMIT"] = CaptureSubject(gitProject).Commit,
                ["TERRABASE_FROZEN_SOURCE_TREE_DIGEST"] = CaptureSubject(gitProject).TreeDigest,
                ["TERRABASE_FROZEN_SOURCE_DIRTY"] = "false",
                ["TERRABASE_FROZEN_SOURCE_WORKING_TREE_DIGEST"] = baseline,
                ["TERRABASE_FROZEN_MANIFEST_PATH"] = Path.GetFullPath(manifestPath),
                ["TERRABASE_FROZEN_MANIFEST_SHA256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant(),
                ["TERRABASE_FROZEN_BINARY_CLOSURE"] = assemblyDirectory,
                ["TERRABASE_FROZEN_BENCHMARK_ASSEMBLY_SHA256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant(),
            };
            var previousEnvironment = frozenEnvironment.Keys.ToDictionary(
                static key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
            try
            {
                foreach (var pair in frozenEnvironment)
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value);

                var frozen = CaptureSubject(gitProject);
                Check("frozen launch identity replaces the primary subject", frozen.Commit == frozen.Frozen?.Commit
                    && frozen.WorkingTreeDigest == baseline
                    && frozen.Dirty == false
                    && frozen.Project == Path.GetFullPath(gitProject), ref failures);
                Check("frozen receipt retains live checkout drift", frozen.Live is { Dirty: true, WorkingTreeDigest: var liveDigest }
                    && liveDigest == changed, ref failures);
                Check("frozen launch identity records manifest and binary closure", frozen.Frozen?.ManifestPath == Path.GetFullPath(manifestPath)
                    && frozen.Frozen.BinaryClosure == assemblyDirectory, ref failures);
            }
            finally
            {
                foreach (var pair in previousEnvironment)
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            try
            {
                var live = CaptureSubject();
                Check(
                    $"live subject capture is well-formed (commit {live.Commit[..12]}, tree {live.TreeDigest[..12]}, source {live.WorkingTreeDigest?[..12]}, dirty {live.Dirty})",
                    IsHex(live.Commit, 40, 64) && IsHex(live.TreeDigest, 40, 64)
                    && live.WorkingTreeDigest is { } digest && IsHex(digest, 64, 64)
                    && Directory.Exists(live.Repository),
                    ref failures);
            }
            catch (InvalidOperationException exception)
            {
                Check($"live subject capture failed: {exception.Message}", success: false, ref failures);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine(failures == 0 ? "Run receipt self-check: PASS" : $"Run receipt self-check: FAIL ({failures} failure(s))");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(string description, bool success, ref int failures)
    {
        Console.WriteLine($"{(success ? "ok" : "FAIL")}: {description}");
        if (!success)
        {
            failures++;
        }
    }

    private static bool IsHex(string value, int minimumLength, int maximumLength) =>
        value.Length >= minimumLength && value.Length <= maximumLength && value.All(Uri.IsHexDigit);

    private static string WorkingTreeDigest(string repository, byte[] workingTreeManifest)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestField(digest, "status", workingTreeManifest);
        AppendDigestField(digest, "diff", GitBytes(repository, "diff --binary HEAD"));

        var untrackedBytes = GitBytes(repository, "ls-files --others --exclude-standard -z");
        var untracked = new List<string>();
        var pathStart = 0;
        for (var index = 0; index <= untrackedBytes.Length; index++)
        {
            if (index != untrackedBytes.Length && untrackedBytes[index] != 0)
            {
                continue;
            }

            if (index > pathStart)
            {
                untracked.Add(Encoding.UTF8.GetString(untrackedBytes, pathStart, index - pathStart));
            }

            pathStart = index + 1;
        }

        untracked.Sort(StringComparer.Ordinal);
        foreach (var relativePath in untracked)
        {
            var pathBytes = Encoding.UTF8.GetBytes(relativePath);
            AppendDigestField(digest, "untracked-path", pathBytes);
            var fullPath = Path.Combine(repository, relativePath.Replace('/', Path.DirectorySeparatorChar));
            AppendDigestField(digest, "untracked-content", File.ReadAllBytes(fullPath));
        }

        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendDigestField(IncrementalHash digest, string name, ReadOnlySpan<byte> value)
    {
        digest.AppendData(Encoding.UTF8.GetBytes(name));
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(length, value.Length);
        digest.AppendData(length);
        digest.AppendData(value);
    }

    private static string Git(string workingDirectory, string arguments)
    {
        return Encoding.UTF8.GetString(GitBytes(workingDirectory, arguments)).Trim();
    }

    private static byte[] GitBytes(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments.Split(' '))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start git in '{workingDirectory}' to resolve the benchmark subject.");
        using var outputBuffer = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(outputBuffer);
        var error = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExitAsync();
        Task.WaitAll(output, error, exited);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments} failed in '{workingDirectory}' (exit {process.ExitCode}): {error.Result.Trim()}");
        }

        return outputBuffer.ToArray();
    }
}

/// <summary>The git identity of the measured subject project.</summary>
public sealed record SubjectProvenance(
    string Project,
    string Repository,
    string Commit,
    string TreeDigest,
    bool Dirty,
    string? WorkingTreeDigest = null,
    IReadOnlyList<string>? WorkingTreeChanges = null,
    SubjectProvenance? Live = null,
    FrozenSourceIdentity? Frozen = null);

/// <summary>
/// Immutable source and artifact identity supplied by a frozen benchmark
/// launcher. The source fields are repeated here so consumers can distinguish
/// the attested identity from the live checkout observation in a receipt.
/// </summary>
public sealed record FrozenSourceIdentity(
    string Project,
    string Repository,
    string Commit,
    string TreeDigest,
    bool Dirty,
    string WorkingTreeDigest,
    string ManifestPath,
    string ManifestSha256,
    string BinaryClosure,
    string BenchmarkAssemblySha256)
{
    private const string Prefix = "TERRABASE_FROZEN_";

    public static FrozenSourceIdentity? ReadFromEnvironment()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(Project)] = Environment.GetEnvironmentVariable(Prefix + "SOURCE_PROJECT"),
            [nameof(Repository)] = Environment.GetEnvironmentVariable(Prefix + "SOURCE_REPOSITORY"),
            [nameof(Commit)] = Environment.GetEnvironmentVariable(Prefix + "SOURCE_COMMIT"),
            [nameof(TreeDigest)] = Environment.GetEnvironmentVariable(Prefix + "SOURCE_TREE_DIGEST"),
            [nameof(Dirty)] = Environment.GetEnvironmentVariable(Prefix + "SOURCE_DIRTY"),
            [nameof(WorkingTreeDigest)] = Environment.GetEnvironmentVariable(Prefix + "SOURCE_WORKING_TREE_DIGEST"),
            [nameof(ManifestPath)] = Environment.GetEnvironmentVariable(Prefix + "MANIFEST_PATH"),
            [nameof(ManifestSha256)] = Environment.GetEnvironmentVariable(Prefix + "MANIFEST_SHA256"),
            [nameof(BinaryClosure)] = Environment.GetEnvironmentVariable(Prefix + "BINARY_CLOSURE"),
            [nameof(BenchmarkAssemblySha256)] = Environment.GetEnvironmentVariable(Prefix + "BENCHMARK_ASSEMBLY_SHA256"),
        };
        var any = values.Values.Any(static value => !string.IsNullOrWhiteSpace(value));
        if (!any)
        {
            return null;
        }

        string Required(string key)
        {
            var value = values[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Frozen benchmark provenance is incomplete: missing {Prefix}{key.ToUpperInvariant()}.");
            }

            return value;
        }

        var project = Required(nameof(Project));
        var repository = Required(nameof(Repository));
        var commit = Required(nameof(Commit));
        var treeDigest = Required(nameof(TreeDigest));
        var dirtyText = Required(nameof(Dirty));
        var workingTreeDigest = Required(nameof(WorkingTreeDigest));
        var manifestPath = Required(nameof(ManifestPath));
        var manifestSha256 = Required(nameof(ManifestSha256));
        var binaryClosure = Required(nameof(BinaryClosure));
        var benchmarkAssemblySha256 = Required(nameof(BenchmarkAssemblySha256));

        if (!Path.IsPathFullyQualified(project) || !Path.IsPathFullyQualified(repository) ||
            !Path.IsPathFullyQualified(manifestPath) || !Path.IsPathFullyQualified(binaryClosure))
        {
            throw new InvalidOperationException("Frozen benchmark provenance paths must be absolute.");
        }

        if (!IsHex(commit, 40) || !IsHex(treeDigest, 40) || !IsHex(workingTreeDigest, 64) ||
            !IsHex(manifestSha256, 64) || !IsHex(benchmarkAssemblySha256, 64) ||
            !bool.TryParse(dirtyText, out var dirty))
        {
            throw new InvalidOperationException("Frozen benchmark provenance contains an invalid digest or sourceDirty value.");
        }

        return new FrozenSourceIdentity(
            project,
            repository,
            commit,
            treeDigest,
            dirty,
            workingTreeDigest,
            manifestPath,
            manifestSha256,
            binaryClosure,
            benchmarkAssemblySha256);
    }

    private static bool IsHex(string value, int length) =>
        value.Length == length && value.All(Uri.IsHexDigit);
}

/// <summary>Machine and toolchain identity available to the process at run time.</summary>
public sealed record ToolchainInfo(
    string MachineName,
    string DotnetVersion,
    string FrameworkDescription,
    string OperatingSystem,
    string Architecture,
    int ProcessorCount,
    string CpuModel,
    string BuildConfiguration)
{
    public static ToolchainInfo Capture()
    {
        var buildConfiguration = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";
        return new ToolchainInfo(
            MachineName: Environment.MachineName,
            DotnetVersion: Environment.Version.ToString(),
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            OperatingSystem: RuntimeInformation.OSDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount: Environment.ProcessorCount,
            CpuModel: ReadCpuModel(),
            BuildConfiguration: buildConfiguration);
    }

    private static string ReadCpuModel()
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                var line = File.ReadLines("/proc/cpuinfo")
                    .FirstOrDefault(line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
                if (line is not null)
                {
                    var separator = line.IndexOf(':');
                    return separator >= 0 ? line[(separator + 1)..].Trim() : line.Trim();
                }
            }
        }
        catch (IOException)
        {
            // The model is optional environment metadata.
        }

        return "unknown";
    }
}

/// <summary>A detailed workload receipt indexed by the compact run receipt.</summary>
public sealed record ReceiptArtifact(string File, long Bytes, string Sha256);

/// <summary>The compact run receipt payload; serialized camelCase with a trailing receiptSha256 digest.</summary>
public sealed record RunReceiptPayload(
    int SchemaVersion,
    string Workload,
    string Status,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    int ExitCode,
    string Command,
    string OutputDirectory,
    SubjectProvenance? Subject,
    ToolchainInfo Toolchain,
    IReadOnlyList<ReceiptArtifact> Artifacts,
    string? Error);
