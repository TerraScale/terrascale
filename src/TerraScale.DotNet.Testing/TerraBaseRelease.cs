using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TerraScale.DotNet.Testing;

/// <summary>
/// A TerraBase release binary (<c>terrabase-linux-amd64</c>) pinned by release tag and SHA-256. Tests run against
/// this real binary, never a fake server: it is the release production TerraBase runs.
/// </summary>
/// <param name="Tag">Release tag in TerraScale/terrabase, for example <c>ci-&lt;commit&gt;-run-&lt;run id&gt;</c> or <c>v0.2.0</c>.</param>
/// <param name="Sha256">Exactly 64 lowercase hexadecimal characters; a download or override with other bytes is refused.</param>
public sealed record TerraBaseRelease(string Tag, string Sha256)
{
    private const string AssetName = "terrabase-linux-amd64";
    // 256 MiB leaves headroom above the pinned 113,200,335-byte binary while bounding download disk use.
    private const int MaxBinaryBytes = 256 * 1024 * 1024;

    /// <summary>
    /// The release this SDK revision is verified against: the CI build of terrabase origin/main
    /// ff256067015433c8c40b59879f1282d2a40480ce. Bump it together with the SDK when TerraBase's contract changes.
    /// </summary>
    public static TerraBaseRelease Pinned { get; } = new(
        "ci-ff256067015433c8c40b59879f1282d2a40480ce-run-13258",
        "e07d173fc2bbab19dcb1fde8b121b0207b07d55452a5b19a406871d220e05e13");

    /// <summary>Where the binary is downloaded from.</summary>
    public Uri DownloadUri => new($"https://git.terrascale.tech/TerraScale/terrabase/releases/download/{Uri.EscapeDataString(Tag)}/{AssetName}");

    /// <summary>
    /// Returns the path of this release's binary, downloading it into the cache on first use. Resolution order:
    /// <list type="number">
    /// <item><c>TERRABASE_BINARY</c>, a binary the caller already has (its SHA-256 must still match);</item>
    /// <item><c>&lt;cache&gt;/&lt;sha256&gt;/terrabase-linux-amd64</c>, verified on every use;</item>
    /// <item>a download with <c>TERRABASE_DOWNLOAD_TOKEN</c> or <c>ORG_READ_TOKEN</c> (terrabase is private).</item>
    /// </list>
    /// The cache is <c>TERRABASE_CACHE_DIRECTORY</c>, else <c>$XDG_CACHE_HOME/terrascale/terrabase</c>, else
    /// <c>~/.cache/terrascale/terrabase</c>. Returns <see langword="null"/> when the binary is neither present nor
    /// downloadable (no token) or this platform cannot run it, with the reason in <c>Unavailable</c>.
    /// Downloads are limited to 256 MiB, checked against both Content-Length and the streamed bytes.
    /// </summary>
    public Task<(string? Path, string? Unavailable)> ResolveAsync(CancellationToken cancellationToken = default) =>
        ResolveAsync(
            Environment.GetEnvironmentVariable("TERRABASE_BINARY"),
            CacheRoot(),
            Environment.GetEnvironmentVariable("TERRABASE_DOWNLOAD_TOKEN") is { Length: > 0 } explicitToken
                ? explicitToken
                : Environment.GetEnvironmentVariable("ORG_READ_TOKEN"),
            cancellationToken);

    internal async Task<(string? Path, string? Unavailable)> ResolveAsync(
        string? binary, string cacheRoot, string? token, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? writeDownloadBody = null, Action<string>? beforePublish = null,
        Func<string, bool>? hasExecuteAccess = null, Action<string>? beforeDeletePartial = null)
    {
        if (Sha256 is not { Length: 64 } || Sha256.Any(character => !char.IsAsciiHexDigitLower(character)))
        {
            throw new ArgumentException("TerraBase SHA-256 must be exactly 64 lowercase hexadecimal characters.", nameof(Sha256));
        }

        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            return (null, $"{AssetName} runs only on linux-x64");
        }

        if (!string.IsNullOrEmpty(binary))
        {
            binary = Path.GetFullPath(binary);
            await VerifyAsync(binary, cancellationToken).ConfigureAwait(false);
            return (binary, null);
        }

        var directory = Path.Combine(Path.GetFullPath(cacheRoot), Sha256);
        var cached = Path.Combine(directory, AssetName);
        if (File.Exists(cached))
        {
            await VerifyAsync(cached, cancellationToken).ConfigureAwait(false);
            return (cached, null);
        }

        if (string.IsNullOrEmpty(token))
        {
            return (null, $"{AssetName} {Tag} is not cached and neither TERRABASE_DOWNLOAD_TOKEN nor ORG_READ_TOKEN is set");
        }

        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, $"{AssetName}.{Guid.NewGuid():N}.partial");
        using var download = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        download.CancelAfter(TimeSpan.FromMinutes(10));
        Exception? failure = null;
        try
        {
            // Tests supply a real binary file body or filesystem fault without replacing HTTP behavior.
            if (writeDownloadBody is not null)
            {
                await writeDownloadBody(partial, download.Token).ConfigureAwait(false);
            }
            else
            {
                using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
                using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUri);
                request.Headers.TryAddWithoutValidation("Authorization", "token " + token);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, download.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Downloading {AssetName} {Tag} answered HTTP {(int)response.StatusCode}.");
                }

                await WriteDownloadAsync(response.Content, partial, download.Token).ConfigureAwait(false);
            }

            await VerifyAsync(partial, download.Token, requireExecutable: false).ConfigureAwait(false);
            File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            beforePublish?.Invoke(partial);
            VerifyExecuteAccess(partial, hasExecuteAccess);
            try
            {
                File.Move(partial, cached);
            }
            catch (IOException) when (File.Exists(cached))
            {
                // Another test process cached it first; its bytes are verified on use like ours.
                await VerifyAsync(cached, download.Token).ConfigureAwait(false);
            }

            return (cached, null);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            DeletePartial(partial, failure, beforeDeletePartial);
        }
    }

    internal static async Task WriteDownloadAsync(
        HttpContent content, string path, CancellationToken cancellationToken, int maxBytes = MaxBinaryBytes)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidOperationException($"TerraBase binary download exceeds the {maxBytes}-byte limit.");
        }

        try
        {
            await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = File.Create(path);
            var buffer = new byte[64 * 1024];
            var total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (read > maxBytes - total)
                {
                    throw new InvalidOperationException($"TerraBase binary download exceeds the {maxBytes}-byte limit.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
            }
        }
        catch (Exception error)
        {
            DeletePartial(path, error);
            throw;
        }
    }

    internal static void DeletePartial(string path, Exception? failure = null, Action<string>? beforeDeletePartial = null)
    {
        try
        {
            beforeDeletePartial?.Invoke(path);
            File.Delete(path);
        }
        catch (Exception cleanupError) when (failure is not null)
        {
            // Keep cancellation/body/digest failures primary, with cleanup diagnostics available to the caller.
            const string key = "TerraBaseCleanupException";
            failure.Data[key] = failure.Data[key] is Exception previous
                ? new AggregateException(previous, cleanupError)
                : cleanupError;
        }
    }

    private async Task VerifyAsync(string path, CancellationToken cancellationToken, bool requireExecutable = true)
    {
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, Sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{path} has SHA-256 {actual}, not the pinned {Sha256} of TerraBase {Tag}.");
        }

        if (requireExecutable) VerifyExecuteAccess(path);
    }

    private void VerifyExecuteAccess(string path, Func<string, bool>? hasExecuteAccess = null)
    {
        // AT_FDCWD, X_OK, AT_EACCESS: ask Linux about the effective user's execute access.
        if (OperatingSystem.IsLinux() && !(hasExecuteAccess?.Invoke(path) ?? CheckExecuteAccess(-100, path, 1, 0x200) == 0))
        {
            throw new InvalidOperationException($"{path} is not executable by the current effective user. Grant appropriate execute and directory traversal permission (for example, chmod u+x); if mounted noexec, move the binary/cache to an executable filesystem before resolving TerraBase {Tag}.");
        }
    }

    [DllImport("libc", EntryPoint = "faccessat", SetLastError = true)]
    private static extern int CheckExecuteAccess(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode, int flags);

    private static string CacheRoot() =>
        Environment.GetEnvironmentVariable("TERRABASE_CACHE_DIRECTORY") is { Length: > 0 } explicitRoot
            ? explicitRoot
            : Path.Combine(
                Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
                "terrascale",
                "terrabase");
}
