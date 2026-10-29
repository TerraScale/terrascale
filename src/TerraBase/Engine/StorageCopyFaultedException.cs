namespace TerraBase.Engine;

internal sealed class StorageCopyFaultedException(string message) : InvalidOperationException(message);
