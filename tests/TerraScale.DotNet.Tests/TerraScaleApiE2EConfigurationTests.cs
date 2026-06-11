namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleApiE2EConfigurationTests
{
    [Test]
    public async Task TerraScaleE2EConfiguration_IsCompleteWhenAnyVariableIsSet()
    {
        var configuredValues = TerraScaleApiE2E.ConfigurationVariables
            .Where(static variable => !string.IsNullOrWhiteSpace(variable.Value))
            .Select(static variable => variable.Name)
            .ToArray();
        var configuredCredentialValues = configuredValues
            .Where(static name => name != "TERRASCALE_E2E_BASE_URL")
            .ToArray();

        if (configuredValues.Length == 0)
        {
            await Assert.That(TerraScaleApiE2E.HasCompleteAuthenticationConfiguration).IsFalse();
            return;
        }

        if (configuredCredentialValues.Length == 0)
        {
            await Assert.That(TerraScaleApiE2E.IsBaseAddressConfigured).IsTrue();
            return;
        }

        if (!TerraScaleApiE2E.HasCompleteAuthenticationConfiguration)
        {
            throw new InvalidOperationException(
                $"Partially configured TerraScale E2E variables: {string.Join(", ", configuredValues)}.");
        }

        await Assert.That(TerraScaleApiE2E.HasCompleteAuthenticationConfiguration).IsTrue();
    }
}
