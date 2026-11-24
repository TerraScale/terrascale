using System.Net.Quic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;

namespace TerraScale.QuicRpc;

public static class QuicPlatform
{
    private const string MsQuicLibrary = "libmsquic.so.2";
    private const string NumaLibrary = "libnuma.so.1";

    private static readonly bool Supported = Probe();

    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("macos")]
    [SupportedOSPlatformGuard("windows")]
    public static bool IsSupported => Supported;

    public static void EnsureInitialized() => _ = Supported;

    private static bool Probe()
    {
        _ = TryLoadBundledMsQuic();
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveBundledMsQuic;
        return QuicListener.IsSupported && QuicConnection.IsSupported;
    }

    private static IntPtr ResolveBundledMsQuic(Assembly assembly, string libraryName)
    {
        if (!OperatingSystem.IsLinux() || !libraryName.StartsWith("libmsquic", StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        return TryLoadBundledMsQuic();
    }

    private static IntPtr TryLoadBundledMsQuic()
    {
        if (!OperatingSystem.IsLinux())
        {
            return IntPtr.Zero;
        }

        var architecture = RuntimeInformation.ProcessArchitecture;
        var runtimeIdentifier = architecture == Architecture.X64 ? "linux-x64"
            : architecture == Architecture.Arm64 ? "linux-arm64"
            : null;
        if (runtimeIdentifier is null)
        {
            return IntPtr.Zero;
        }

        foreach (var directory in SearchDirectories(runtimeIdentifier))
        {
            var candidate = Path.Combine(directory, MsQuicLibrary);
            if (!File.Exists(candidate))
            {
                continue;
            }

            var numa = Path.Combine(directory, NumaLibrary);
            if (File.Exists(numa))
            {
                _ = NativeLibrary.TryLoad(numa, out _);
            }

            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        _ = NativeLibrary.TryLoad(NumaLibrary, typeof(QuicPlatform).Assembly, DllImportSearchPath.AssemblyDirectory, out _);
        return NativeLibrary.TryLoad(MsQuicLibrary, typeof(QuicPlatform).Assembly, DllImportSearchPath.AssemblyDirectory, out var extracted)
            ? extracted
            : IntPtr.Zero;
    }

    private static List<string> SearchDirectories(string runtimeIdentifier)
    {
        List<string> directories = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string search)
        {
            foreach (var entry in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(entry);
                Add(Path.Combine(entry, "runtimes", runtimeIdentifier, "native"));
            }
        }

        Add(AppContext.BaseDirectory);
        Add(Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeIdentifier, "native"));
        return directories;

        void Add(string directory)
        {
            if (directory.Length > 0 && seen.Add(directory))
            {
                directories.Add(directory);
            }
        }
    }
}
