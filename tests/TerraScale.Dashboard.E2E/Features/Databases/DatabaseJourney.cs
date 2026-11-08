using System.Net;
using Microsoft.AspNetCore.Builder;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    // Shared steps for journeys that need a freshly onboarded account on a DatabaseSdkScenario host pair.
    internal static class DatabaseJourney
    {
        internal static async Task<(string Email, string Password)> NativeWorkspaceAsync(HttpClient client, string? existingEmail = null, string? existingPassword = null)
        {
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var email = existingEmail ?? "sdk-signup-" + suffix + "@example.test";
            var password = existingPassword ?? "SDK-signup-" + suffix;
            // Accounts are created on ts-auth's hosted sign-up; the callback starts the dashboard session.
            using (var verified = await HttpJourney.LoginAsync(client, email, password, "/onboarding", signUp: existingEmail is null))
            {
                Check(verified.StatusCode == HttpStatusCode.SeeOther, "Hosted sign-up bootstraps canonical producer organization/project IDs through the official management SDK");
            }

            await StepAsync(new() { ["step"] = "account-information", ["displayName"] = "Customer SDK " + suffix });
            await StepAsync(new() { ["step"] = "payment-method", ["plan"] = "free" });
            await StepAsync(new() { ["step"] = "project", ["name"] = "Customer SDK project", ["region"] = "GRU" });
            return (email, password);

            async Task StepAsync(Dictionary<string, string> form)
            {
                form["_csrf"] = await HttpJourney.CsrfAsync(client, "/onboarding");
                using var response = await HttpJourney.PostFormAsync(client, "/onboarding/step", form);
                Check(response.StatusCode == HttpStatusCode.SeeOther && response.Headers.Location?.OriginalString is "/onboarding" or "/",
                    "Native onboarding advances the actual current account step: " + form["step"]);
            }
        }

        // Each scenario owns one TestAuth shared by its host pair; tests read its exchange grants to check bearers exactly.
        internal static Action<WebApplicationBuilder> CaptureAuth(Action<TestAuth> captured)
        {
            return application =>
            captured((TestAuth)application.Services.Last(item => item.ServiceType == typeof(IAuthGateway)).ImplementationInstance!);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
