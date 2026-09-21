namespace TerraScale.Dashboard.Features.Settings.Models;

public sealed record WebhookEndpoint(
    string Id,
    string ProjectId,
    string Url,
    string Status,
    string CreatedAt,
    string SecretPreview);
