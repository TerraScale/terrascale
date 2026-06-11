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

        if (configuredValues.Length == 0)
        {
            await Assert.That(TerraScaleApiE2E.IsConfigured).IsFalse();
            return;
        }

        if (!TerraScaleApiE2E.IsConfigured)
        {
            throw new InvalidOperationException(
                $"Partially configured TerraScale E2E variables: {string.Join(", ", configuredValues)}.");
        }

        await Assert.That(TerraScaleApiE2E.IsConfigured).IsTrue();
    }
}
