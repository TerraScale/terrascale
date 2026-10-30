using TerraScale.Dashboard.E2E;

// Await synthetic host modes outside module initialization. All normal invocations use the generated TUnit helper.
var fixtureMarker = Array.IndexOf(args, "--dashboard-fixture-host");
if (fixtureMarker >= 0)
{
    return await DashboardFixtureEntryPoint.RunAsync(args, fixtureMarker);
}

var stripeMarker = Array.IndexOf(args, "--stripe-audit-test-host");
if (stripeMarker >= 0)
{
    return await StripeAuditCliEntryPoint.RunAsync(args, stripeMarker);
}

return await MicrosoftTestingPlatformApplication.RunAsync(args);
