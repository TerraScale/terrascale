using System.Net;

namespace TerraScale.Dashboard.Features.Data
{
    // OperationId identifies a dashboard attempt; it is not a producer idempotency key.
    public sealed class DocumentWriteOutcomeUnknownException(string operationId, string documentId, string operation)
        : DatabaseProviderException(HttpStatusCode.ServiceUnavailable,
            $"Write outcome unknown. Reload the document to reconcile before retrying. Attempt {operationId}; document {documentId}.")
    {
        public string OperationId { get; } = operationId;
        public string DocumentId { get; } = documentId;
        public string Operation { get; } = operation;
    }
}
