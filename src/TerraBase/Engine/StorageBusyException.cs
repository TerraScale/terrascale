namespace TerraBase.Engine;

public sealed class StorageBusyException() : Exception("storage is busy; retry later");
