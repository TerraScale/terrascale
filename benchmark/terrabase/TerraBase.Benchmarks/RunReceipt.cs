using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>Captures the provenance of the TerraBase subject this runner was built against.</summary>
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
        return new SubjectProvenance(
            Project: Path.GetFullPath(project),
            Repository: repository,
            Commit: Git(repository, "rev-parse HEAD"),
            TreeDigest: Git(repository, "rev-parse HEAD^{tree}"),
            Dirty: Git(repository, "status --porcelain").Length != 0);
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

            var computed = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(document.ToJsonString(CanonicalJson)))).ToLowerInvariant();
            return string.Equals(recordedDigest, computed, StringComparison.Ordinal);
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
            var subject = new SubjectProvenance("/self/check/TerraBase.csproj", "/self/check", new string('a', 40), new string('b', 40), Dirty: true);
            var receiptPath = Write(
                root, "self-check", "complete", 0, startedAtUtc, startedAtUtc.AddSeconds(1), subject);
            Check("receipt verifies after write", Verify(receiptPath), ref failures);

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

            try
            {
                var live = CaptureSubject();
                Check(
                    $"live subject capture is well-formed (commit {live.Commit[..12]}, tree {live.TreeDigest[..12]}, dirty {live.Dirty})",
                    IsHex(live.Commit, 40, 64) && IsHex(live.TreeDigest, 40, 64) && Directory.Exists(live.Repository),
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

    private static string Git(string workingDirectory, string arguments)
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
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments} failed in '{workingDirectory}' (exit {process.ExitCode}): {process.StandardError.ReadToEnd().Trim()}");
        }

        return process.StandardOutput.ReadToEnd().Trim();
    }
}

/// <summary>The git identity of the measured subject project.</summary>
public sealed record SubjectProvenance(
    string Project,
    string Repository,
    string Commit,
    string TreeDigest,
    bool Dirty);

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
