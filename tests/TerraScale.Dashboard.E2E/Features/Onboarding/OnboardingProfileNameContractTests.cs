using TerraScale.Dashboard.Features.Onboarding;

namespace TerraScale.Dashboard.E2E.Features.Onboarding
{
    public sealed class OnboardingProfileNameContractTests
    {
        [Test]
        [Arguments("mariogk+qa4-newuser1", "mariogk+qa4-newuser1@terrascale.tech", "")]
        [Arguments("Mariogk+QA4-Newuser1", "mariogk+qa4-newuser1@terrascale.tech", "")]
        [Arguments("mariogk+qa4-newuser1@terrascale.tech", "mariogk+qa4-newuser1@terrascale.tech", "")]
        [Arguments("Ada Lovelace", "ada@example.com", "Ada Lovelace")]
        public async Task FullNameDefaultsOnlyToARealProfileName(string name, string email, string expected)
        {
            await Assert.That(OnboardingEndpoints.ProfileName(name, email)).IsEqualTo(expected)
                .Because("The email or its handle is ts-auth's placeholder, not the user's full name.");
        }
    }
}
