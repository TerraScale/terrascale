namespace TerraBase.Engine.Turso;

internal sealed record StorageEngineOptions(
    string DataDirectory,
    int Threads,
    int Queue,
    long MemoryBudget,
    TimeSpan ShutdownGrace,
    int? MaxWriters = null,
    TimeSpan? ReadinessCacheTtl = null,
    TimeSpan? QueryBudget = null,
    TimeProvider? Time = null,
    TimeProvider? BackupTime = null);
