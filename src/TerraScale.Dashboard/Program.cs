using TerraScale.Dashboard.Demo;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;

// The server only: audit and internal billing commands always run against the real providers.
if (PublicDemo.Enabled && (args.Length == 0 || args[0].StartsWith('-')))
    return await PublicDemo.RunAsync();
if (args.FirstOrDefault() == "audit-stripe")
    return await new StripeAuditCommand(new DashboardIdentity()).RunAsync(args[1..]);
if (args.FirstOrDefault() == "setup-stripe")
    return await new StripeSetupCommand(new DashboardIdentity()).RunAsync(args[1..]);

return await InternalBillingCommands.RunAsync(args);
