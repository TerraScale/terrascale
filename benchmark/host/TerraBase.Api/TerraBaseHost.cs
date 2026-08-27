// <copyright file="TerraBaseHost.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text.Json.Serialization;

/// <summary>
/// The API host assembly point: builds the ASP.NET Core application
/// over one supervised engine endpoint. The production entry point
/// (<see cref="Program"/>) and the test host share it, so the tested
/// surface is the served surface.
/// </summary>
public static class TerraBaseHost
{
    /// <summary>Builds the host application over one engine endpoint.</summary>
    /// <param name="options">The engine endpoint and restart policy.</param>
    /// <param name="configure">An optional builder hook (test wiring).</param>
    /// <returns>The application with its supervised engine, stream hub, and audit log.</returns>
    public static HostBundle Create(EngineOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.ListenUrl);
        configure?.Invoke(builder);
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var audit = new AuditLog();
        var supervisor = new EngineSupervisor(options, audit);
        var hub = new BackboneHub(supervisor, audit);
        var app = builder.Build();
        TypedApi.MapApi(app, supervisor, hub, audit);
        return new HostBundle(app, supervisor, hub, audit);
    }
}

/// <summary>The built host: the application and its engine-facing halves.</summary>
/// <param name="App">The ASP.NET Core application.</param>
/// <param name="Supervisor">The engine supervisor.</param>
/// <param name="Hub">The backbone stream hub.</param>
/// <param name="Audit">The lifecycle audit log.</param>
public sealed record HostBundle(WebApplication App, EngineSupervisor Supervisor, BackboneHub Hub, AuditLog Audit);
