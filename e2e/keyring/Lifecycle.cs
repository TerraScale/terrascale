using System.Text.Json;
using TerraBase.Configuration;

namespace Journey.Keyring;

internal static class Lifecycle
{
    private const int Usage = 2;

    private static int Main(string[] args)
    {
        EncryptedFile.UseServiceNatives();
        return TerraBaseOptions.FromProcess([]) switch
        {
            TerraBaseOptions options => Run(new DataLayout(options), args),
            ConfigurationError error => Refuse(string.Concat("the node configuration was refused: ", error.Message)),
        };
    }

    private static int Run(DataLayout layout, string[] args)
    {
        var keys = MasterKeys.FromProcess();
        if (layout.ConfiguredActiveKeyId != keys.Active)
        {
            return Refuse("the node and the proof disagree on the active master key");
        }

        if (args is ["inventory", var output])
        {
            InventoryProof.Write(layout, keys, output);
            return 0;
        }

        if (args is ["evidence", var request])
        {
            var evidence = JsonSerializer.Deserialize(File.ReadAllText(request), ProofJsonContext.Default.EvidenceRequest)
                ?? throw new InvalidOperationException("the evidence request is empty");
            FileEvidence.Prove(layout, keys, evidence);
            return 0;
        }

        return Refuse("usage: inventory <output.json> | evidence <request.json>");
    }

    private static int Refuse(string message)
    {
        Console.Error.WriteLine(string.Concat("keyring-driver:refused ", message));
        return Usage;
    }
}
