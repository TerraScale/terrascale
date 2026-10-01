using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TerraScale.DotNet.Native;

// Walk from an opened root, validating each inode before traversing its entries.
// Root-owned sticky ancestors are allowed; the final directory must be private
// against entry replacement. Intermediate links are resolved only from a
// protected parent, including the system-controlled /var and /tmp macOS links.
internal static partial class NativeDirectoryPath
{
    internal static SafeFileHandle Open(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Directory-descriptor persistence requires Unix.");
        var pending = new LinkedList<string>(Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries));
        var parent = NativeFilePersistence.OpenUnix("/", directory: true);
        var links = 0;
        try
        {
            NativeFilePersistence.ValidateDirectory(parent, allowRootSticky: pending.Count != 0);
            while (pending.First is { } first)
            {
                var component = first.Value;
                pending.RemoveFirst();
                if (component == ".") continue;
                var final = pending.Count == 0;
                var descriptor = OpenAt(parent, component, NativeFilePersistence.ReadFlags(directory: true));
                if (descriptor < 0)
                {
                    if (final) throw new InvalidDataException("Native operation directory must be a directory without a symbolic link.");
                    if ((File.GetUnixFileMode(parent) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                        throw new InvalidDataException("Native operation hierarchy cannot follow links from a writable directory.");
                    var buffer = new byte[4096];
                    var size = ReadLinkAt(parent, component, buffer, (nuint)buffer.Length);
                    if (size < 0) throw NativeFilePersistence.IoError("Cannot open native operation ancestor.");
                    if (size >= buffer.Length || ++links > 40)
                        throw new InvalidDataException("Native operation hierarchy has an oversized or looping link.");
                    string target;
                    try { target = new UTF8Encoding(false, true).GetString(buffer, 0, (int)size); }
                    catch (DecoderFallbackException exception) { throw new InvalidDataException("Native operation directory link must be UTF-8.", exception); }
                    if (target.StartsWith('/'))
                    {
                        parent.Dispose();
                        parent = NativeFilePersistence.OpenUnix("/", directory: true);
                        NativeFilePersistence.ValidateDirectory(parent, allowRootSticky: true);
                    }
                    foreach (var item in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
                        pending.AddFirst(item);
                    continue;
                }
                var next = new SafeFileHandle((nint)descriptor, ownsHandle: true);
                try { NativeFilePersistence.ValidateDirectory(next, allowRootSticky: !final); }
                catch { next.Dispose(); throw; }
                parent.Dispose();
                parent = next;
            }
            NativeFilePersistence.ValidateDirectory(parent);
            return parent;
        }
        catch { parent.Dispose(); throw; }
    }

    // No O_CREAT: these calls have no variadic mode argument.
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(SafeFileHandle parent, string path, int flags);

    [LibraryImport("libc", EntryPoint = "readlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint ReadLinkAt(SafeFileHandle parent, string path, [Out] byte[] buffer, nuint size);
}
