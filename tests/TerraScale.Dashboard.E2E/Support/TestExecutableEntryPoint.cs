namespace TerraScale.Dashboard.E2E
{
    // The executable's only Main. Synthetic host switches are awaited here before TUnit starts; anything else runs the
    // generated testing-platform helper, exactly as the generated entry point did.
    internal static class TestExecutableEntryPoint
    {
        public static async Task<int> Main(string[] args)
        {
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
        }
    }
}
