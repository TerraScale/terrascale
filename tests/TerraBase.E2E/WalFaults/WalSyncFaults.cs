using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace TerraBase.E2E.WalFaults;

/// <summary>
/// Test-only faults on the write-ahead log syncs of one node process: delay or fail every sync, SIGKILL at the n-th, and
/// a power loss that drops what was never synced. <c>walfaults.c</c>, built here with the host's C compiler, does the
/// work inside the process through <c>LD_PRELOAD</c>; production code and configuration know nothing of it.
/// </summary>
/// <param name="directory">This process's control directory, outside its data directory (TerraBase lists that one).</param>
internal sealed class WalSyncFaults(string directory)
{
    private static readonly Lazy<string?> Shim = new(Build);

    /// <summary>The built shim, or null without one: not Linux, or no <c>cc</c> on the path. A failed build throws.</summary>
    public static string? Library => Shim.Value;

    public string Directory { get; } = directory;

    private string SyncedFile => Path.Combine(Directory, "synced");

    /// <summary>Loads the shim into a process about to start, with this directory as its control.</summary>
    public void AddTo(IDictionary<string, string?> environment)
    {
        environment["LD_PRELOAD"] = Library ?? throw new InvalidOperationException("no write-ahead log fault shim: needs Linux and cc");
        environment["WALFAULTS_DIR"] = Directory;
        _ = System.IO.Directory.CreateDirectory(Directory);
    }

    // Without a wal every log of the process counts, Turso's temporary ones too (tursodb-temp.db-wal outside the data
    // directory): give the database's log to aim at one sync.

    /// <summary>Every sync of the process's logs (or of <paramref name="wal"/>) waits <paramref name="delay"/> first.</summary>
    public void Delay(TimeSpan delay, string? wal = null) => Arm($"delay {(long)delay.TotalMilliseconds}", wal);

    /// <summary>Every sync fails with <paramref name="errno"/> (5 is EIO) and syncs nothing.</summary>
    public void Fail(int errno, string? wal = null) => Arm($"fail {errno}", wal);

    /// <summary>SIGKILL at the entry of the <paramref name="sync"/>-th sync from now: what it would have synced is written, not synced.</summary>
    public void KillAtSync(int sync, string? wal = null) => Arm($"kill-at-sync {sync}", wal);

    /// <summary>Syncs run normally again.</summary>
    public void Clear() => Arm("none 0", null);

    /// <summary>The length of <paramref name="wal"/> at its last successful sync, 0 if it never synced.</summary>
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

    /// <summary>
    /// A power loss of the stopped process: every write-ahead log under <paramref name="dataDirectory"/> goes back to its
    /// length at its last sync. shortcut: only the logs are cut, whole; torn pages and unsynced main database writes after a
    /// checkpoint are not simulated, and a log restarted by a checkpoint keeps its unsynced frames until its next sync —
    /// model those when a scenario needs them.
    /// </summary>
    public void DropUnsynced(string dataDirectory)
    {
        foreach (var wal in System.IO.Directory.EnumerateFiles(dataDirectory, "*-wal", SearchOption.AllDirectories))
        {
            using var file = new FileStream(wal, FileMode.Open, FileAccess.Write);
            file.SetLength(Math.Min(file.Length, SyncedLength(wal)));
        }
    }

    // Moved in, not rewritten: the shim restarts its sync count on a new file.
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

        // Moved in: a process that loaded the previous build keeps its own copy.
        File.Move(built, library, overwrite: true);
        return library;
    }
}
