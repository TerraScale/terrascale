using System.Text.Json;
using TerraScale.DotNet.Native;
using TerraScale.DotNet.Qualification;

await NativeChecks.ContractResourcesAsync();
await NativeChecks.TransportOwnershipAsync();
await NativeChecks.PublicNativeJourneyAsync();
await NativeChecks.DirectOriginsAsync();
await NativeChecks.AuthDiagnosticsAsync();

// Independently accepted producer examples are read from the installed SDK resource.
var scope = new NativeScope("018f0000-0000-7000-8000-000000000001",
    "018f0000-0000-7000-8000-000000000002", 0);
using (var stream = typeof(NativeCodec).Assembly.GetManifestResourceStream("TerraScale.DotNet.Native.client-openapi.json")
    ?? throw new InvalidOperationException("Installed native OpenAPI resource is missing."))
using (var openApi = await JsonDocument.ParseAsync(stream))
{
    var examples = openApi.RootElement.GetProperty("paths")
        .GetProperty("/v1/tenants/{tenant}/databases/{database}/shards/{shard}/transactions")
        .GetProperty("post").GetProperty("requestBody").GetProperty("content")
        .GetProperty("application/json").GetProperty("examples");
    (string Name, string Digest)[] accepted =
    [
        ("create", "e0434f6594433812ae108095baaddb29e2365b63e40d3b0147076668ae12b57f"),
        ("update", "dc301f7cd4f7f458bb4ae6436b8cfde1d732f9d7994f85bb47e31eec966722be"),
        ("delete", "61f146edbd4c6a1bb18f8f3ced5c1b03884f17798e0ab0a0c35e757e4a745fd4")
    ];
    foreach (var example in accepted)
    {
        var request = examples.GetProperty(example.Name).GetProperty("value");
        if (request.GetProperty("effect_digest").GetString() != example.Digest)
            throw new InvalidOperationException("Installed OpenAPI differs from an accepted producer digest.");
        var commit = NativeCodec.DecodeCommit(scope, request.GetRawText());
        if (NativeCodec.EncodeCommit(scope, commit).Identity.EffectDigest != example.Digest)
            throw new InvalidOperationException("Installed SDK differs from a real producer example digest.");
        var changedIdentity = commit with
        {
            TransactionId = new NativeTransactionId(ulong.MaxValue, new string('f', 32))
        };
        if (NativeCodec.EncodeCommit(scope, changedIdentity).Identity.EffectDigest != example.Digest)
            throw new InvalidOperationException("Effect digest changed with transaction identity.");
    }
}
Console.WriteLine("Native SDK public package consumer passed.");
