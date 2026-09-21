namespace TerraScale.Auth.Webhooks;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;

/// <summary>Request body for webhook endpoint creation.</summary>
internal sealed record CreateWebhookRequest(string? Url, string[]? Events);
