using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;

return args.FirstOrDefault() switch
{
    "audit-stripe" => await new StripeAuditCommand(new DashboardIdentity()).RunAsync(args[1..]),
    "setup-stripe" => await new StripeSetupCommand(new DashboardIdentity()).RunAsync(args[1..]),
    _ => await InternalBillingCommands.RunAsync(args)
};
