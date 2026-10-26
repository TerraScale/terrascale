using System.Runtime.InteropServices;
using TerraBase.Configuration;

namespace TerraBase.Engine;

internal sealed partial class NativeTemporaryStorage(TerraBaseOptions options) : INativeTemporaryStorage
{
    private static readonly Lock Gate = new();
    private static string? _path;

    public void Initialize()
    {
        var root = Path.GetFullPath(options.DataDirectory);
        var path = Path.Combine(root, ".tmp");
        lock (Gate)
        {
            if (_path is not null)
            {
                if (!string.Equals(_path, path, StringComparison.Ordinal))
                {
                    throw new TerraBaseConfigurationException("native temporary storage cannot change data-directory within a process");
                }

                return;
            }

            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("native temporary storage requires Linux");
            }

            Directory.CreateDirectory(root);
            // The engine holds its data lock before calling us; never clean again in this process.
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new TerraBaseConfigurationException("native temporary storage data-directory must not be a symbolic link");
            }

            if (Directory.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new TerraBaseConfigurationException("native temporary storage must not be a symbolic link");
                }

                Directory.Delete(path, recursive: true);
            }

            Directory.CreateDirectory(path);
            // Rust's tempfile reads libc getenv; Unix's managed environment dictionary is insufficient.
            if (SetEnvironment("TMPDIR", path, 1) != 0)
            {
                throw new IOException($"initializing native temporary storage failed (errno {Marshal.GetLastPInvokeError()})");
            }

            _path = path;
        }
    }

    [LibraryImport("libc", EntryPoint = "setenv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SetEnvironment(string name, string value, int overwrite);
}
