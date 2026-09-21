namespace TerraScale.Auth.Webhooks;


internal static class WebhooksModule
{
    internal static void Start(WebApplication app) => _ = Task.Run(() =>
        app.Services.GetRequiredService<WebhookDispatcher>().RunAsync(app.Lifetime.ApplicationStopping));

    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(CreateWebhookEndpoint),
        typeof(ListWebhooksEndpoint),
        typeof(ListDeadLettersEndpoint),
        typeof(ReplayDeadLetterEndpoint),
        typeof(DeleteWebhookEndpoint),
        typeof(TestWebhookEndpoint),
        typeof(ListAuditEventsEndpoint),
    ];
}
