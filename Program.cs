using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;

if (args.FirstOrDefault() == "audit-stripe")
    return await new StripeAuditCommand(new DashboardIdentity()).RunAsync(args[1..]);

return await InternalBillingCommands.RunAsync(args);
