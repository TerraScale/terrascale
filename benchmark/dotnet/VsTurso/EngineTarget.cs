using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Tenancy;

namespace TerraBase.E2E
{
    internal sealed class EngineTarget(IDocumentService documents, TenantContext database, string collection) : ITarget
    {
        private static readonly Precondition None = new(null, IfMatchAny: false, IfNoneMatch: false);

        public async Task PutAsync(string id, byte[] json)
        {
            var outcome = await documents.WriteConfirmedAsync(database, [new PutOperation(collection, id, json, None)], indexErrors: false, CancellationToken.None);
            if (outcome is not WriteResult { Durability: WriteResult.LocalDurable })
            {
                throw new InvalidOperationException($"engine: {id} was not written: {Describe(outcome)}");
            }
        }

        private static string Describe(WriteOutcome outcome)
        {
            return outcome switch
            {
                WriteResult written => written.Durability,
                DocumentRejected rejected => rejected.Error switch
                {
                    NotFound error => error.ToString(),
                    Conflict error => error.ToString(),
                    Validation error => error.ToString(),
                    Unavailable error => error.ToString(),
                    OutcomeUnknown error => error.ToString(),
                    _ => "rejected",
                },
            };
        }

        public async Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> batch)
        {
            for (var first = 0; first < batch.Count; first += 100)
            {
                var operations = batch.Skip(first).Take(100).Select(document => (DocumentOperation)new PutOperation(collection, document.Id, document.Json, None)).ToList();
                if (await documents.WriteConfirmedAsync(database, operations, indexErrors: false, CancellationToken.None) is var outcome && outcome is not WriteResult)
                {
                    throw new InvalidOperationException($"engine: the preload was not written: {Describe(outcome)}");
                }
            }
        }

        public async Task GetAsync(string id)
        {
            if (await documents.GetAsync(new DocumentKey(database, collection, id), CancellationToken.None) is not StoredDocument)
            {
                throw new InvalidOperationException($"engine: {id} is missing");
            }
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
