namespace TerraBase.Engine;

internal sealed class StorageProofPendingException() : InvalidOperationException("storage recovery proof is pending; retry");
