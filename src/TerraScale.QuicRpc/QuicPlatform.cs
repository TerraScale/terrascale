using System.Net.Quic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;

namespace TerraScale.QuicRpc;

public static class QuicPlatform
{
    private const string MsQuicLibrary = "libmsquic.so.2";

    private static readonly bool Supported = Probe();

    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("macos")]
    [SupportedOSPlatformGuard("windows")]
    public static bool IsSupported => Supported;

    private static bool Probe()
    {
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveBundledMsQuic;
        return QuicListener.IsSupported && QuicConnection.IsSupported;
    }

    private static IntPtr ResolveBundledMsQuic(Assembly assembly, string libraryName)
    {
        if (!OperatingSystem.IsLinux() || !libraryName.StartsWith("libmsquic", StringComparison.Ordinal))
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

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, MsQuicLibrary),
            Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeIdentifier, "native", MsQuicLibrary),
        ];
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }
}
