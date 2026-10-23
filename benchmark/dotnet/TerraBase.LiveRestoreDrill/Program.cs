using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Configuration;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Backup;
using TerraBase.LiveRestoreDrill;

var proof = new Proof();
string? scratch = null;
SqliteStorageEngine? engine = null;
byte[]? master = null;
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(30));
Console.CancelKeyPress += (_, signal) => { signal.Cancel = true; stop.Cancel(); };
using var terminate = OperatingSystem.IsLinux()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => { signal.Cancel = true; stop.Cancel(); })
    : null;
try
{
    if (!OperatingSystem.IsLinux())
    {
        throw new PlatformNotSupportedException();
    }

    var arguments = args.Select(argument => argument.Split('=', 2)).ToArray();
    string[] names = ["--bucket", "--endpoint", "--refs-directory", "--file-prefix", "--snapshot-key"];
    if (arguments.Length != names.Length || arguments.Any(parts => parts.Length != 2 || !names.Contains(parts[0], StringComparer.Ordinal))
        || arguments.Select(parts => parts[0]).Distinct(StringComparer.Ordinal).Count() != names.Length)
    {
        throw new ArgumentException("expected five nonsecret arguments");
    }

    var values = arguments.ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
    var bucket = values["--bucket"];
    var file = values["--file-prefix"];
    var parts = file.Split('/');
    if (bucket.Length is < 3 or > 63 || bucket.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
        || values["--endpoint"].TrimEnd('/') != ReadOnlyTransport.Endpoint
        || parts.Length != 4 || parts[0] != "v1" || parts.Skip(1).Any(part => !Ulid.TryParse(part, out var id) || id.ToString() != part))
    {
        throw new ArgumentException("invalid bucket, pinned endpoint, or canonical user database prefix");
    }

    var scope = parts[1] + "/" + parts[2];
    proof.Bucket = bucket;
    proof.FilePrefix = file;
    proof.KeyScope = scope;
    proof.Stage = "credentials";
    var refs = Path.GetFullPath(values["--refs-directory"]);
    var access = ReadSecret(refs, "aws_access_key_id_file");
    var secret = ReadSecret(refs, "aws_secret_access_key_file");
    master = Convert.FromBase64String(ReadSecret(refs, "terrabase_master_key_file"));
    if (master.Length != 32)
    {
        throw new InvalidDataException("invalid master key length");
    }

    // Fixed /tmp, independent of TMPDIR or production data-directory settings; create with 0700 from the start.
    scratch = PrivateScratch.Create();
    var options = new TerraBaseOptions { DataDirectory = scratch, MemoryBudget = 64L << 20, StorageThreads = 1, StorageQueue = 32 };
    using var meter = new Meter("TerraBase.LiveRestoreDrill");
    var services = new ServiceCollection();
    engine = new SqliteStorageEngine(options.DataDirectory, 1, 32, options.MemoryBudget, meter,
        NullLogger<SqliteStorageEngine>.Instance, TimeSpan.FromSeconds(10));
    services.AddHttpClient(S3ObjectStore.HttpClientName)
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        .AddHttpMessageHandler(() => new ReadOnlyTransport(bucket));
    await using var provider = services.BuildServiceProvider();
    using var store = new S3ObjectStore(new Uri(ReadOnlyTransport.Endpoint), "auto", bucket, access, secret,
        provider.GetRequiredService<IHttpClientFactory>(), S3ObjectStore.HttpClientName, TimeProvider.System);
    var readOnly = new ReadOnlyStore(store, file, scope);
    await readOnly.CheckWriteRejectionAsync().ConfigureAwait(false);
    proof.WriteRejectionSelfCheck = true;

    var settings = new BackupSettings
    {
        Endpoint = new Uri(ReadOnlyTransport.Endpoint),
        Region = "auto",
        Bucket = bucket,
        AccessKeyId = access,
        SecretKey = secret,
        MasterKey = master,
    };
    using var keys = new DataKeys(readOnly, new MasterKey(master), TimeProvider.System, settings);
    var reader = new BackupReader(engine, readOnly, keys, options);
    proof.Stage = "snapshot_selection";
    SnapshotKey? latest = null;
    await foreach (var snapshot in reader.SnapshotsAsync(file, stop.Token).ConfigureAwait(false))
    {
        if (latest is null || snapshot.At > latest.At || (snapshot.At == latest.At && string.CompareOrdinal(snapshot.Key, latest.Key) > 0))
        {
            latest = snapshot;
        }
    }

    if (latest is null || (values["--snapshot-key"] != "latest" && values["--snapshot-key"] != latest.Key))
    {
        throw new InvalidDataException("no latest snapshot or reviewed snapshot is stale");
    }

    var observed = DateTimeOffset.UtcNow;
    if (latest.At > observed)
    {
        throw new InvalidDataException("snapshot time is in the future");
    }

    var source = latest;
    await foreach (var snapshot in reader.SnapshotsAsync(file, stop.Token).ConfigureAwait(false))
    {
        if (snapshot.Generation == latest.Generation && snapshot.Position < latest.Position && snapshot.At <= latest.At
            && (source == latest || snapshot.Position > source.Position))
        {
            source = snapshot;
        }
    }

    proof.SnapshotKey = latest.Key;
    proof.SourceSnapshotKey = source.Key;
    proof.Generation = latest.Generation.ToString();
    proof.SnapshotAt = latest.At;
    proof.ObservedAt = observed;
    proof.SnapshotRpoMs = (observed - latest.At).TotalMilliseconds;
    proof.TargetPosition = latest.Position;
    proof.Stage = "reference_download";
    var baseline = Path.Combine(scratch, "reference.db");
    var started = Stopwatch.GetTimestamp();
    await using (var output = new FileStream(baseline, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
    {
        if (!await readOnly.ReadAsync(latest.Key,
            (input, token) => BackupObject.ReadAsync(input, output, latest.Key, id => keys.GetAsync(scope, id, token), token), stop.Token).ConfigureAwait(false))
        {
            throw new InvalidDataException("reference snapshot disappeared");
        }
    }

    proof.ReferenceDownloadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    proof.ExpectedDigest = Convert.ToHexString(await engine.RestoreDrillChecksumAsync(baseline, stop.Token).ConfigureAwait(false));
    proof.Stage = "restore";
    started = Stopwatch.GetTimestamp();
    var restored = await reader.BuildAsync(file, scope, source, DateTimeOffset.MaxValue, latest.Position, stop.Token).ConfigureAwait(false);
    proof.DownloadRestoreMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    proof.ReachedPosition = restored.Position;
    proof.EncryptedObjectAuthentication = true;
    proof.DatabaseIntegrityCheck = true;
    proof.WalBytesReplayed = restored.Position - source.Position;
    proof.WalExercised = proof.WalBytesReplayed > 0;
    proof.Stage = "checksum";
    proof.RestoredDigest = Convert.ToHexString(await engine.RestoreDrillChecksumAsync(restored.Path, stop.Token).ConfigureAwait(false));
    stop.Token.ThrowIfCancellationRequested();
    proof.DigestMatch = proof.ExpectedDigest == proof.RestoredDigest;
    proof.Passed = restored.Position == latest.Position && proof.DigestMatch;
    proof.Stage = proof.Passed ? "complete" : "same_cut_mismatch";
}
catch (OperationCanceledException)
{
    proof.Passed = false;
    proof.Stage = "cancelled";
}
catch (Exception)
{
    // SDK, filesystem and decoder errors can contain data, credentials or signed request fields.
    proof.Passed = false;
    if (proof.Stage == "complete")
    {
        proof.Stage = "disposal_failed";
    }
}
finally
{
    if (master is not null)
    {
        CryptographicOperations.ZeroMemory(master);
    }

    try
    {
        await ScratchCleanup.DeleteAsync(engine, scratch).ConfigureAwait(false);
        proof.DisposableCleanup = scratch is null || !Directory.Exists(scratch);
        if (stop.IsCancellationRequested)
        {
            proof.Passed = false;
            proof.Stage = "cancelled";
        }
    }
    catch (Exception)
    {
        proof.Passed = false;
        proof.Stage = "cleanup_failed";
    }
}

Console.WriteLine(JsonSerializer.Serialize(proof, ProofJson.Default.Proof));
return proof.Passed && proof.DisposableCleanup ? 0 : 1;

static string ReadSecret(string directory, string name)
{
    if (!OperatingSystem.IsLinux())
    {
        throw new PlatformNotSupportedException();
    }

    const UnixFileMode publicPermissions = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    var info = new FileInfo(Path.Combine(directory, name));
    var invalid = new DirectoryInfo(directory).LinkTarget is not null || (File.GetUnixFileMode(directory) & publicPermissions) != 0
        || info.LinkTarget is not null || info.Length is < 1 or > 4096 || (File.GetUnixFileMode(info.FullName) & publicPermissions) != 0;
    return invalid
        ? throw new InvalidDataException("invalid delivered secret file")
        : File.ReadAllText(info.FullName).TrimEnd('\r', '\n');
}
