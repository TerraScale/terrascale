// <copyright file="EngineStatus.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The supervisor's lifecycle states.</summary>
public enum EngineStatus
{
    /// <summary>The engine endpoint is being established.</summary>
    Starting,

    /// <summary>The engine channel is serving.</summary>
    Serving,

    /// <summary>The engine endpoint is lost (or was never established); every caller hears the typed refusal.</summary>
    Unavailable,

    /// <summary>The host stopped the engine deliberately.</summary>
    Stopped,
}
