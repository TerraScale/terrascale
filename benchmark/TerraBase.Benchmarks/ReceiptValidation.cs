using System.Globalization;
using System.Text.Json;

namespace TerraBase.Benchmarks;

/// <summary>
/// Validates the persisted benchmark receipt contract without depending on the
/// runner's private result types.
/// </summary>
/// <remarks>
/// A receipt is admitted as a performance claim only when it is complete. An
/// unsupported receipt may be structurally valid, but it can never be admitted
/// and it must not contain measured performance fields.
/// </remarks>
public static class ReceiptValidation
{
    /// <summary>The minimum number of measured post-warmup samples.</summary>
    public const int MinimumMeasuredSamples = 5;

    /// <summary>Validates a receipt JSON file.</summary>
    public static ReceiptValidationResult ValidateFile(
        string receiptPath,
        int minimumSamples = MinimumMeasuredSamples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        if (!File.Exists(receiptPath))
        {
            return Failure($"Receipt file does not exist: '{receiptPath}'.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(receiptPath));
            return Validate(document.RootElement, minimumSamples);
        }
        catch (JsonException exception)
        {
            return Failure($"Receipt is not valid JSON: {exception.Message}");
        }
        catch (IOException exception)
        {
            return Failure($"Receipt could not be read: {exception.Message}");
        }
    }

    /// <summary>Validates a JSON receipt string.</summary>
    public static ReceiptValidationResult ValidateJson(
        string json,
        int minimumSamples = MinimumMeasuredSamples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            return Validate(document.RootElement, minimumSamples);
        }
        catch (JsonException exception)
        {
            return Failure($"Receipt is not valid JSON: {exception.Message}");
        }
    }

    /// <summary>Validates one parsed receipt object.</summary>
    public static ReceiptValidationResult Validate(
        JsonElement receipt,
        int minimumSamples = MinimumMeasuredSamples)
    {
        var errors = new List<string>();
        if (minimumSamples < 1)
        {
            errors.Add("minimumSamples must be at least one.");
            minimumSamples = MinimumMeasuredSamples;
        }

        if (receipt.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Receipt root must be a JSON object.");
            return Result(errors, status: null);
        }

        var status = StringProperty(receipt, "status", errors, required: true);
        if (status is null)
        {
            return Result(errors, status: null);
        }

        if (status is not ("complete" or "failed" or "invalid" or "unsupported"))
        {
            errors.Add($"status '{status}' is not one of complete, failed, invalid, or unsupported.");
            return Result(errors, status);
        }

        if (status == "unsupported")
        {
            ValidateUnsupportedReceipt(receipt, errors);
            return Result(errors, status);
        }

        var requireClaimIdentity = status == "complete";
        ValidateCommonIdentity(receipt, errors, requireClaimIdentity);
        if (requireClaimIdentity)
        {
            ValidateConfiguration(receipt, errors);
        }

        if (TryObject(receipt, "workloads", out var workloads))
        {
            ValidateStorageReceipt(receipt, workloads, status, minimumSamples, errors);
        }
        else if (status == "complete")
        {
            errors.Add("A complete receipt must contain a workloads array.");
        }

        // Compression and subscription receipts use cases rather than the
        // storage runner's workloads array. Keep unsupported cases claim-free;
        // their workload-specific correctness remains owned by that harness.
        if (TryArray(receipt, "cases", out var cases))
        {
            ValidateCases(cases, status, errors);
        }

        return Result(errors, status);
    }

    private static void ValidateCommonIdentity(
        JsonElement receipt,
        List<string> errors,
        bool requireClaimIdentity)
    {
        if (!TryObject(receipt, "environment", out var environment))
        {
            if (requireClaimIdentity)
            {
                errors.Add("A performance receipt must contain environment identity.");
            }

            return;
        }

        if (requireClaimIdentity)
        {
            RequiredText(environment, "runtime", errors, "environment");
            RequiredText(environment, "frameworkDescription", errors, "environment");
            RequiredText(environment, "buildConfiguration", errors, "environment");
            RequiredText(environment, "operatingSystem", errors, "environment");
            RequiredText(environment, "architecture", errors, "environment");
            RequiredText(environment, "cpuModel", errors, "environment");
            RequiredText(environment, "gcLatencyMode", errors, "environment");
            RequiredText(environment, "gitCommit", errors, "environment");
            RequiredText(environment, "artifactPath", errors, "environment");
        }

        var build = StringProperty(environment, "buildConfiguration", errors, required: false);
        if (requireClaimIdentity && !string.Equals(build, "Release", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("A performance receipt must identify a Release artifact.");
        }

        var artifact = StringProperty(environment, "artifactSha256", errors, required: requireClaimIdentity);
        if (requireClaimIdentity && artifact is not null && !IsSha256(artifact))
        {
            errors.Add("environment.artifactSha256 must be a 64-character hexadecimal SHA-256 digest.");
        }

        var commit = StringProperty(environment, "gitCommit", errors, required: false);
        if (requireClaimIdentity && commit is not null && !IsHex(commit, 40, 64))
        {
            errors.Add("environment.gitCommit must be a 40- or 64-character hexadecimal revision.");
        }

        if (requireClaimIdentity && !TryBoolean(environment, "gitDirty", out _))
        {
            errors.Add("environment.gitDirty must record whether the source tree was dirty.");
        }

        var processorCount = NumberProperty(environment, "processorCount", errors, required: requireClaimIdentity);
        if (requireClaimIdentity && (!processorCount.HasValue || processorCount.Value < 1 || processorCount.Value != Math.Truncate(processorCount.Value)))
        {
            errors.Add("environment.processorCount must be a positive integer.");
        }

        var memory = NumberProperty(environment, "availableMemoryBytes", errors, required: requireClaimIdentity);
        if (requireClaimIdentity && (!memory.HasValue || memory.Value <= 0 || memory.Value != Math.Truncate(memory.Value)))
        {
            errors.Add("environment.availableMemoryBytes must be a positive integer.");
        }

        if (requireClaimIdentity && !TryBoolean(environment, "serverGc", out _))
        {
            errors.Add("environment.serverGc must be recorded.");
        }

        if (requireClaimIdentity)
        {
            RequiredText(receipt, "command", errors, "receipt");
            RequiredText(receipt, "receiptDirectory", errors, "receipt");
        }
    }

    private static void ValidateConfiguration(JsonElement receipt, List<string> errors)
    {
        if (!TryObject(receipt, "configuration", out var configuration))
        {
            errors.Add("A complete receipt must contain configuration identity.");
            return;
        }

        RequiredText(configuration, "storage", errors, "configuration");
        RequiredText(configuration, "outputDirectory", errors, "configuration");
        foreach (var name in new[] { "datasetSize", "payloadSize", "samples", "warmupOperations", "operationsPerSample", "concurrency" })
        {
            var number = NumberProperty(configuration, name, errors, required: true, "configuration");
            if (number.HasValue && number.Value != Math.Truncate(number.Value))
            {
                errors.Add($"configuration.{name} must be an integer.");
            }
        }

        var datasetSize = ConfigurationNumber(receipt, "datasetSize");
        var payloadSize = ConfigurationNumber(receipt, "payloadSize");
        var samples = ConfigurationNumber(receipt, "samples");
        var operations = ConfigurationNumber(receipt, "operationsPerSample");
        var concurrency = ConfigurationNumber(receipt, "concurrency");
        if (datasetSize is not > 0)
        {
            errors.Add("configuration.datasetSize must be positive.");
        }

        if (payloadSize is not > 0)
        {
            errors.Add("configuration.payloadSize must be positive.");
        }

        if (samples is not >= 1)
        {
            errors.Add("configuration.samples must be positive.");
        }

        if (operations is not > 0)
        {
            errors.Add("configuration.operationsPerSample must be positive.");
        }

        if (concurrency is not > 0)
        {
            errors.Add("configuration.concurrency must be positive.");
        }

        if (!TryArray(configuration, "workloads", out var workloads) || workloads.GetArrayLength() == 0)
        {
            errors.Add("configuration.workloads must contain at least one workload.");
        }

        if (!TryArray(configuration, "temperatures", out var temperatures) || temperatures.GetArrayLength() == 0)
        {
            errors.Add("configuration.temperatures must contain at least one temperature.");
        }
    }

    private static void ValidateStorageReceipt(
        JsonElement receipt,
        JsonElement workloads,
        string status,
        int minimumSamples,
        List<string> errors)
    {
        if (workloads.GetArrayLength() == 0)
        {
            errors.Add("A receipt must contain at least one workload result.");
            return;
        }

        var configuredSamples = ConfigurationNumber(receipt, "samples");
        if (status == "complete" && configuredSamples.HasValue && configuredSamples.Value < minimumSamples)
        {
            errors.Add($"configuration.samples must be at least {minimumSamples}.");
        }

        var configuredOperations = ConfigurationNumber(receipt, "operationsPerSample");
        var configuredConcurrency = ConfigurationNumber(receipt, "concurrency");
        var configuredStorage = ConfigurationText(receipt, "storage");

        var workloadIndex = 0;
        foreach (var workload in workloads.EnumerateArray())
        {
            var path = $"workloads[{workloadIndex}]";
            workloadIndex++;
            if (workload.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path} must be an object.");
                continue;
            }

            var workloadStatus = StringProperty(workload, "status", errors, required: true, path);
            var storage = StringProperty(workload, "storage", errors, required: true, path);
            var workloadName = StringProperty(workload, "workload", errors, required: true, path);
            RequiredText(workload, "temperature", errors, path);
            RequiredText(workload, "rawSamplesPath", errors, path);

            var digest = StringProperty(workload, "workloadDigest", errors, required: status == "complete", path);
            if (status == "complete" && digest is not null && !IsSha256(digest))
            {
                errors.Add($"{path}.workloadDigest must be a 64-character hexadecimal SHA-256 digest.");
            }

            if (configuredStorage is not null && storage is not null &&
                !string.Equals(configuredStorage, storage, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{path}.storage '{storage}' does not match configuration.storage '{configuredStorage}'.");
            }

            if (status == "complete" && !string.Equals(workloadStatus, "complete", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{path}.status must be complete when the receipt status is complete.");
            }

            if (!TryArray(workload, "samples", out var samples))
            {
                if (status == "complete")
                {
                    errors.Add($"{path}.samples must be an array.");
                }

                continue;
            }

            if (status == "complete")
            {
                if (samples.GetArrayLength() < minimumSamples)
                {
                    errors.Add($"{path}.samples contains {samples.GetArrayLength()} samples; at least {minimumSamples} are required.");
                }

                if (configuredSamples.HasValue && samples.GetArrayLength() != configuredSamples.Value)
                {
                    errors.Add($"{path}.samples count must equal configuration.samples ({configuredSamples.Value}).");
                }
            }

            var sampleIds = new HashSet<int>();
            var sampleIndex = 0;
            foreach (var sample in samples.EnumerateArray())
            {
                var samplePath = $"{path}.samples[{sampleIndex}]";
                sampleIndex++;
                ValidateSample(
                    sample,
                    samplePath,
                    storage,
                    status,
                    configuredOperations,
                    configuredConcurrency,
                    processorCount: EnvironmentProcessorCount(receipt),
                    sampleIds,
                    errors);
            }

            if (status == "complete" && storage is not null && workloadName is not null)
            {
                ValidateWalDefinition(storage, workloadName, samples, path, errors);
            }
        }
    }

    private static void ValidateSample(
        JsonElement sample,
        string path,
        string? storage,
        string receiptStatus,
        double? configuredOperations,
        double? configuredConcurrency,
        double? processorCount,
        HashSet<int> sampleIds,
        List<string> errors)
    {
        if (sample.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path} must be an object.");
            return;
        }

        var sampleNumber = NumberProperty(sample, "sample", errors, required: receiptStatus == "complete", path);
        if (sampleNumber.HasValue && (sampleNumber.Value != Math.Truncate(sampleNumber.Value) || !sampleIds.Add((int)sampleNumber.Value)))
        {
            errors.Add($"{path}.sample must be a unique positive integer.");
        }

        var operations = NumberProperty(sample, "operations", errors, required: receiptStatus == "complete", path);
        if (receiptStatus == "complete" && (!operations.HasValue || operations.Value <= 0 || operations.Value != Math.Truncate(operations.Value)))
        {
            errors.Add($"{path}.operations must be a positive integer.");
        }

        var concurrency = NumberProperty(sample, "concurrency", errors, required: receiptStatus == "complete", path);
        if (receiptStatus == "complete" && (!concurrency.HasValue || concurrency.Value <= 0 || concurrency.Value != Math.Truncate(concurrency.Value)))
        {
            errors.Add($"{path}.concurrency must be a positive integer.");
        }

        if (configuredOperations.HasValue && operations.HasValue && operations != configuredOperations)
        {
            errors.Add($"{path}.operations must equal configuration.operationsPerSample ({configuredOperations.Value.ToString(CultureInfo.InvariantCulture)}).");
        }

        if (configuredConcurrency.HasValue && concurrency.HasValue && concurrency != configuredConcurrency)
        {
            errors.Add($"{path}.concurrency must equal configuration.concurrency ({configuredConcurrency.Value.ToString(CultureInfo.InvariantCulture)}).");
        }

        var p50 = NumberProperty(sample, "p50Microseconds", errors, required: receiptStatus == "complete", path);
        var p95 = NumberProperty(sample, "p95Microseconds", errors, required: receiptStatus == "complete", path);
        var p99 = NumberProperty(sample, "p99Microseconds", errors, required: receiptStatus == "complete", path);
        if (receiptStatus == "complete" &&
            (!NonNegativeFinite(p50) || !NonNegativeFinite(p95) || !NonNegativeFinite(p99)))
        {
            errors.Add($"{path} must contain finite, non-negative p50/p95/p99 values.");
        }

        if (p50.HasValue && p95.HasValue && p50 > p95 || p95.HasValue && p99.HasValue && p95 > p99)
        {
            errors.Add($"{path} percentile values must satisfy p50 <= p95 <= p99.");
        }

        var elapsed = NumberProperty(sample, "elapsedMilliseconds", errors, required: receiptStatus == "complete", path);
        var throughput = NumberProperty(sample, "throughputOpsPerSecond", errors, required: receiptStatus == "complete", path);
        if (receiptStatus == "complete" && (!PositiveFinite(elapsed) || !NonNegativeFinite(throughput)))
        {
            errors.Add($"{path} elapsedMilliseconds must be positive and throughputOpsPerSecond must be finite and non-negative.");
        }

        var allocated = NumberProperty(sample, "allocatedBytes", errors, required: receiptStatus == "complete", path);
        var allocatedPerOperation = NumberProperty(sample, "allocatedBytesPerOperation", errors, required: receiptStatus == "complete", path);
        if (receiptStatus == "complete" && (!NonNegativeFinite(allocated) || !NonNegativeFinite(allocatedPerOperation)))
        {
            errors.Add($"{path} allocation values must be finite and non-negative.");
        }

        if (operations is > 0 && allocated.HasValue && allocatedPerOperation.HasValue &&
            !CloseEnough(allocatedPerOperation.Value, allocated.Value / operations.Value))
        {
            errors.Add($"{path}.allocatedBytesPerOperation must equal allocatedBytes / operations.");
        }

        var cpuMilliseconds = NumberProperty(sample, "processCpuMilliseconds", errors, required: receiptStatus == "complete", path);
        var cpuPercent = NumberProperty(sample, "processCpuPercent", errors, required: receiptStatus == "complete", path);
        if (receiptStatus == "complete" && (!NonNegativeFinite(cpuMilliseconds) || !NonNegativeFinite(cpuPercent)))
        {
            errors.Add($"{path} CPU values must be finite and non-negative.");
        }

        if (cpuMilliseconds.HasValue && elapsed is > 0 && processorCount is > 0 && cpuPercent.HasValue &&
            !CloseEnough(cpuPercent.Value, cpuMilliseconds.Value / elapsed.Value / processorCount.Value * 100d))
        {
            errors.Add($"{path}.processCpuPercent must be process CPU milliseconds / elapsed milliseconds / processor count * 100.");
        }

        var workingSetBefore = NumberProperty(sample, "processWorkingSetBytesBefore", errors, required: receiptStatus == "complete", path);
        var workingSetAfter = NumberProperty(sample, "processWorkingSetBytesAfter", errors, required: receiptStatus == "complete", path);
        var workingSetDelta = NumberProperty(sample, "processWorkingSetDeltaBytes", errors, required: receiptStatus == "complete", path);
        if (receiptStatus == "complete" &&
            (!NonNegativeFinite(workingSetBefore) || !NonNegativeFinite(workingSetAfter) || !Finite(workingSetDelta)))
        {
            errors.Add($"{path} working-set values must be finite and non-negative except for the signed delta.");
        }

        if (workingSetBefore.HasValue && workingSetAfter.HasValue && workingSetDelta.HasValue &&
            !CloseEnough(workingSetDelta.Value, workingSetAfter.Value - workingSetBefore.Value))
        {
            errors.Add($"{path}.processWorkingSetDeltaBytes must equal after minus before.");
        }

        var logicalBytes = NumberProperty(sample, "logicalBytes", errors, required: receiptStatus == "complete", path);
        var returnedBytes = NumberProperty(sample, "returnedBytes", errors, required: receiptStatus == "complete", path);
        if (receiptStatus == "complete" && (!NonNegativeFinite(logicalBytes) || !NonNegativeFinite(returnedBytes)))
        {
            errors.Add($"{path} logicalBytes and returnedBytes must be finite and non-negative.");
        }

        var bytesWritten = NullableNumberProperty(sample, "bytesWritten", errors, path);
        var writeAmplification = NullableNumberProperty(sample, "writeAmplification", errors, path);
        if (bytesWritten.HasValue && (!NonNegativeFinite(bytesWritten) || bytesWritten.Value != Math.Truncate(bytesWritten.Value)))
        {
            errors.Add($"{path}.bytesWritten must be a non-negative integer when present.");
        }

        if (writeAmplification.HasValue && !NonNegativeFinite(writeAmplification))
        {
            errors.Add($"{path}.writeAmplification must be finite and non-negative when present.");
        }

        if (string.Equals(storage, "StorageEngine", StringComparison.OrdinalIgnoreCase))
        {
            if (writeAmplification.HasValue)
            {
                if (!bytesWritten.HasValue || bytesWritten.Value <= 0 || !logicalBytes.HasValue || logicalBytes.Value <= 0 ||
                    !CloseEnough(writeAmplification.Value, bytesWritten.Value / logicalBytes.Value))
                {
                    errors.Add($"{path}.writeAmplification must equal WAL bytes written / logical bytes.");
                }
            }
            else if (bytesWritten is > 0 && logicalBytes is > 0)
            {
                errors.Add($"{path}.writeAmplification is required when positive WAL bytes and a logical-byte denominator are measured.");
            }
        }
        else if (bytesWritten.HasValue || writeAmplification.HasValue)
        {
            errors.Add($"{path} cannot claim WAL bytes or write amplification for storage '{storage ?? "unknown"}'.");
        }
    }

    private static void ValidateWalDefinition(
        string storage,
        string workload,
        JsonElement samples,
        string path,
        List<string> errors)
    {
        if (!string.Equals(storage, "StorageEngine", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // A WAL measurement is meaningful for a write boundary. For reads and
        // scans, zero bytes is a valid observed delta and a null ratio carries
        // no amplification claim.
        if (!string.Equals(workload, "PointWrite", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var index = 0;
        foreach (var sample in samples.EnumerateArray())
        {
            if (sample.ValueKind == JsonValueKind.Object &&
                NullableNumberProperty(sample, "bytesWritten", errors, $"{path}.samples[{index}]") is null)
            {
                // A missing WAL observation is allowed to represent an
                // unavailable instrument; it is never converted into zero.
            }

            index++;
        }
    }

    private static void ValidateUnsupportedReceipt(JsonElement receipt, List<string> errors)
    {
        var hasReason = false;
        foreach (var name in new[] { "unsupportedReason", "error" })
        {
            if (StringProperty(receipt, name, errors, required: false) is { Length: > 0 })
            {
                hasReason = true;
            }
        }

        if (TryArray(receipt, "workloads", out var workloads))
        {
            foreach (var workload in workloads.EnumerateArray())
            {
                if (workload.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("Unsupported workload entries must be objects.");
                    continue;
                }

                var status = StringProperty(workload, "status", errors, required: true, "workloads");
                hasReason |= StringProperty(workload, "error", errors, required: false) is { Length: > 0 };
                hasReason |= StringProperty(workload, "unsupportedReason", errors, required: false) is { Length: > 0 };
                if (status is not null && !string.Equals(status, "unsupported", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("An unsupported receipt cannot contain a measured or failed workload claim.");
                }

                if (TryArray(workload, "samples", out var samples) && samples.GetArrayLength() != 0)
                {
                    errors.Add("Unsupported workloads must not contain samples.");
                }
            }
        }

        if (TryArray(receipt, "cases", out var cases))
        {
            foreach (var item in cases.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("Unsupported case entries must be objects.");
                    continue;
                }

                var status = StringProperty(item, "status", errors, required: true, "cases");
                hasReason |= StringProperty(item, "error", errors, required: false) is { Length: > 0 };
                hasReason |= StringProperty(item, "unsupportedReason", errors, required: false) is { Length: > 0 };
                if (status is not null && !string.Equals(status, "unsupported", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("An unsupported receipt cannot contain a measured or failed case claim.");
                }

                foreach (var metric in new[]
                {
                    "p50Microseconds", "p95Microseconds", "p99Microseconds", "allocatedBytes",
                    "processCpuMilliseconds", "processWorkingSetBytesBefore", "processWorkingSetBytesAfter",
                    "bytesWritten", "writeAmplification",
                })
                {
                    if (item.TryGetProperty(metric, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    {
                        errors.Add($"Unsupported cases must not contain measured field '{metric}'.");
                    }
                }
            }
        }

        if (!hasReason)
        {
            errors.Add("An unsupported receipt must retain an unsupported reason.");
        }
    }

    private static void ValidateCases(JsonElement cases, string receiptStatus, List<string> errors)
    {
        var index = 0;
        foreach (var item in cases.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"cases[{index}] must be an object.");
                index++;
                continue;
            }

            var caseStatus = StringProperty(item, "status", errors, required: true, $"cases[{index}]");
            if (receiptStatus == "complete" && !string.Equals(caseStatus, "complete", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"cases[{index}].status must be complete when the receipt status is complete.");
            }

            index++;
        }
    }

    private static double? EnvironmentProcessorCount(JsonElement receipt)
    {
        return TryObject(receipt, "environment", out var environment) &&
            TryNumber(environment, "processorCount", out var count)
            ? count
            : null;
    }

    private static double? ConfigurationNumber(JsonElement receipt, string name)
    {
        return TryObject(receipt, "configuration", out var configuration) &&
            TryNumber(configuration, name, out var value)
            ? value
            : null;
    }

    private static string? ConfigurationText(JsonElement receipt, string name)
    {
        return TryObject(receipt, "configuration", out var configuration) &&
            TryString(configuration, name, out var value)
            ? value
            : null;
    }

    private static ReceiptValidationResult Result(List<string> errors, string? status)
    {
        return new ReceiptValidationResult(
            IsValid: errors.Count == 0,
            CanClaim: errors.Count == 0 && string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase),
            Status: status,
            Errors: errors.AsReadOnly());
    }

    private static ReceiptValidationResult Failure(string error) =>
        new(false, false, null, new[] { error });

    private static void RequiredText(JsonElement objectElement, string name, List<string> errors, string path)
    {
        StringProperty(objectElement, name, errors, required: true, path);
    }

    private static string? StringProperty(
        JsonElement objectElement,
        string name,
        List<string> errors,
        bool required,
        string path = "receipt")
    {
        if (!TryGetProperty(objectElement, name, out var value))
        {
            if (required)
            {
                errors.Add($"{path}.{name} is required.");
            }

            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}.{name} must be a string.");
            return null;
        }

        var text = value.GetString();
        if (required && string.IsNullOrWhiteSpace(text))
        {
            errors.Add($"{path}.{name} must not be empty.");
            return null;
        }

        return text;
    }

    private static double? NumberProperty(
        JsonElement objectElement,
        string name,
        List<string> errors,
        bool required,
        string path)
    {
        if (!TryGetProperty(objectElement, name, out var value))
        {
            if (required)
            {
                errors.Add($"{path}.{name} is required.");
            }

            return null;
        }

        if (!TryNumber(value, out var number))
        {
            errors.Add($"{path}.{name} must be a JSON number.");
            return null;
        }

        return number;
    }

    private static double? NullableNumberProperty(
        JsonElement objectElement,
        string name,
        List<string> errors,
        string path)
    {
        if (!TryGetProperty(objectElement, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (!TryNumber(value, out var number))
        {
            errors.Add($"{path}.{name} must be a JSON number or null.");
            return null;
        }

        return number;
    }

    private static double? NumberProperty(JsonElement objectElement, string name, List<string> errors, bool required, string path, bool unused = false) =>
        NumberProperty(objectElement, name, errors, required, path);

    private static bool TryObject(JsonElement objectElement, string name, out JsonElement value) =>
        TryGetProperty(objectElement, name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryArray(JsonElement objectElement, string name, out JsonElement value) =>
        TryGetProperty(objectElement, name, out value) && value.ValueKind == JsonValueKind.Array;

    private static bool TryBoolean(JsonElement objectElement, string name, out bool value) =>
        TryGetProperty(objectElement, name, out var property) && property.ValueKind == JsonValueKind.True
            ? (value = true) == true
            : TryGetProperty(objectElement, name, out property) && property.ValueKind == JsonValueKind.False
                ? (value = false) == false
                : (value = false) && false;

    private static bool TryNumber(JsonElement objectElement, string name, out double value) =>
        TryGetProperty(objectElement, name, out var property) && TryNumber(property, out value);

    private static bool TryNumber(JsonElement value, out double number)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out number))
        {
            number = 0;
            return false;
        }

        return double.IsFinite(number);
    }

    private static bool TryString(JsonElement objectElement, string name, out string value)
    {
        if (TryGetProperty(objectElement, name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetProperty(JsonElement objectElement, string name, out JsonElement value)
    {
        if (objectElement.ValueKind == JsonValueKind.Object && objectElement.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in objectElement.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool NonNegativeFinite(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0;

    private static bool PositiveFinite(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value > 0;

    private static bool Finite(double? value) => value.HasValue && double.IsFinite(value.Value);

    private static bool CloseEnough(double actual, double expected)
    {
        var tolerance = Math.Max(1e-6, Math.Abs(expected) * 1e-6);
        return Math.Abs(actual - expected) <= tolerance;
    }

    private static bool IsSha256(string value) => IsHex(value, 64, 64);

    private static bool IsHex(string value, int minimumLength, int maximumLength) =>
        value.Length >= minimumLength && value.Length <= maximumLength && value.All(Uri.IsHexDigit);
}

/// <summary>Machine-readable result of receipt validation.</summary>
public sealed record ReceiptValidationResult(
    bool IsValid,
    bool CanClaim,
    string? Status,
    IReadOnlyList<string> Errors)
{
    /// <summary>True only when validation passed and the receipt can be admitted as a claim.</summary>
    public bool IsAdmissible => IsValid && CanClaim;
}
