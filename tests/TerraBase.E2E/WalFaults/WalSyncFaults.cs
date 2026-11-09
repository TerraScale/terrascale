using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace TerraBase.E2E.WalFaults;

internal sealed class WalSyncFaults(string directory)
{
    private static readonly Lazy<string?> Shim = new(Build);

    public static string? Library => Shim.Value;

    public string Directory { get; } = directory;

    private string SyncedFile => Path.Combine(Directory, "synced");

    public void AddTo(IDictionary<string, string?> environment)
    {
        environment["LD_PRELOAD"] = Library ?? throw new InvalidOperationException("no write-ahead log fault shim: needs Linux and cc");
        environment["WALFAULTS_DIR"] = Directory;
        _ = System.IO.Directory.CreateDirectory(Directory);
    }

    public void Delay(TimeSpan delay, string? wal = null) => Arm($"delay {(long)delay.TotalMilliseconds}", wal);

    public void Fail(int errno, string? wal = null) => Arm($"fail {errno}", wal);

    public void KillAtSync(int sync, string? wal = null) => Arm($"kill-at-sync {sync}", wal);

    public void Clear() => Arm("none 0", null);

    public long SyncedLength(string wal)
    {
        var path = Path.GetFullPath(wal);
        long length = 0;
        if (File.Exists(SyncedFile))
        {
            foreach (var line in File.ReadLines(SyncedFile))
            {
                var space = line.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0 && string.Equals(line[(space + 1)..], path, StringComparison.Ordinal))
                {
                    length = long.Parse(line[..space], CultureInfo.InvariantCulture);
                }
            }
        }

        return length;
    }

    public void DropUnsynced(string dataDirectory)
    {
        foreach (var wal in System.IO.Directory.EnumerateFiles(dataDirectory, "*-wal", SearchOption.AllDirectories))
        {
            using var file = new FileStream(wal, FileMode.Open, FileAccess.Write);
            file.SetLength(Math.Min(file.Length, SyncedLength(wal)));
        }
    }

    private void Arm(string mode, string? wal)
    {
        _ = System.IO.Directory.CreateDirectory(Directory);
        var control = Path.Combine(Directory, "control");
        File.WriteAllText(control + ".partial", wal is null ? mode : $"{mode} {Path.GetFullPath(wal)}");
        File.Move(control + ".partial", control, overwrite: true);
    }

    private static string? Build()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var source = Path.Combine(AppContext.BaseDirectory, "WalFaults", "walfaults.c");
        var library = Path.ChangeExtension(source, ".so");
        var built = $"{library}.{Environment.ProcessId}";
        var start = new ProcessStartInfo("cc") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in (string[])["-shared", "-fPIC", "-O2", "-Wall", "-Wextra", "-Werror", "-o", built, source, "-ldl"])
        {
            start.ArgumentList.Add(argument);
        }

        Process compiler;
        try
        {
            compiler = Process.Start(start)!;
        }
        catch (Win32Exception)
        {
            return null;
        }

        using (compiler)
        {
            var errors = compiler.StandardError.ReadToEnd() + compiler.StandardOutput.ReadToEnd();
            compiler.WaitForExit();
            if (compiler.ExitCode != 0)
            {
                throw new InvalidOperationException($"cc could not build {source}:\n{errors}");
            }
        }

        File.Move(built, library, overwrite: true);
        return library;
    }
}
