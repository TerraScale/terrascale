using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Client for project audit-event endpoints.
/// </summary>
public sealed class TerraScaleAuditClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleAuditClient(TerraScaleClient client) => this.client = client;

    /// <summary>
    /// Lists audit events for a project.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleProjectAuditEvent>> ListAsync(
        string projectId,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleProjectAuditEvent>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectAuditList(projectId, skip, take),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Gets one project audit event by id.
    /// </summary>
    public Task<TerraScaleProjectAuditEvent> GetAsync(
        string projectId,
        string auditEventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditEventId);
        return client.SendAsync<TerraScaleProjectAuditEvent>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectAuditGet(projectId, auditEventId),
            body: null,
            cancellationToken);
    }
}

/// <summary>
/// A project audit event returned by the public API.
/// </summary>
public sealed record TerraScaleProjectAuditEvent(
    [property: JsonRequired] string AuditEventId,
    [property: JsonRequired] string ProjectId,
    [property: JsonRequired] string Actor,
    [property: JsonRequired] string Action,
    [property: JsonRequired] string Target,
    [property: JsonRequired] string Risk,
    [property: JsonRequired] string Source,
    [property: JsonRequired] DateTimeOffset OccurredAt) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (AuditEventId is null ||
            ProjectId is null ||
            Actor is null ||
            Action is null ||
            Target is null ||
            Risk is null ||
            Source is null)
        {
            throw new JsonException("TerraScale project audit event contains a null required field.");
        }
    }
}
