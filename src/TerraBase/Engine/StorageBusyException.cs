using System.Runtime.CompilerServices;

namespace TerraBase.Engine;

public sealed class StorageBusyException([CallerFilePath] string file = "", [CallerLineNumber] int line = 0) : Exception("storage is busy; retry later")
{
    public string At { get; } = $"{Path.GetFileName(file)}:{line}";
}
