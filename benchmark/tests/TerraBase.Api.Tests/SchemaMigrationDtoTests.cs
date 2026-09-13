// <copyright file="SchemaMigrationDtoTests.cs" company="TerraScale">
// TerraBase (SM-04). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

using System.Text.Json;

/// <summary>The closed input mappings used by the schema and migration API.</summary>
public sealed class SchemaMigrationDtoTests
{
    /// <summary>A literal must select exactly one of the documented discriminators.</summary>
    [Test]
    public async Task ADefaultLiteralWithMultipleDiscriminatorsIsRejected()
    {
        var literal = new LiteralDto { Boolean = false, Integer = 0 };

        var failure = await Assert.ThrowsAsync<JsonException>(() => Task.Run(literal.ToLiteral));

        await Assert.That(failure.Message).Contains("exactly one discriminator");
    }

    /// <summary>A false boolean remains a valid selected literal.</summary>
    [Test]
    public async Task AFalseBooleanLiteralMapsToTheBooleanVariant()
    {
        var literal = new LiteralDto { Boolean = false }.ToLiteral();

        await Assert.That(literal.Variant).IsEqualTo("Boolean");
        await Assert.That(literal.Payload).IsEqualTo(false);
    }
}
