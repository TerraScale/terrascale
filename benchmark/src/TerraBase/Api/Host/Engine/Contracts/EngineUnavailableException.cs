// <copyright file="EngineUnavailableException.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The typed, clean refusal every caller hears while the engine generation is down.</summary>
public sealed class EngineUnavailableException(string message) : Exception(message);
