using System.Runtime.InteropServices;
using System.Text;

namespace TerraBase.LiveRestoreDrill
{
    internal static class PrivateScratch
    {
        internal static string Create()
        {
            // mkdtemp creates a new 0700 directory atomically, refusing existing files and links.
            var template = Encoding.UTF8.GetBytes("/tmp/terrabase-live-restore-XXXXXX\0");
            return Mkdtemp(template) != IntPtr.Zero
                ? Encoding.UTF8.GetString(template.AsSpan(0, template.Length - 1))
                : throw new IOException("could not create private drill scratch");
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport("libc", EntryPoint = "mkdtemp", SetLastError = true)]
        private static extern IntPtr Mkdtemp([In, Out] byte[] template);
    }
}
