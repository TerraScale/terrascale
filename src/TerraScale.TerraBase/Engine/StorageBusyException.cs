namespace TerraBase.Engine;

/// <summary>
/// The engine already runs as much storage work as it admits (<c>storage-queue</c>); retry later.
/// The HTTP listeners answer it with 503 and <c>Retry-After</c>.
/// </summary>
public sealed class StorageBusyException() : Exception("storage is busy; retry later");
