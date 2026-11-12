using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Features.ProjectTenants;

public abstract record ProjectCaller
{
    private ProjectCaller() { }

    public abstract string Id { get; }

    public abstract UserRecord Person { get; }

    public sealed record SignedIn(UserRecord User) : ProjectCaller
    {
        public override string Id => User.Id;

        public override UserRecord Person => User;
    }

    public sealed record SecretKey(ApiKeyRecord Key) : ProjectCaller
    {
        public override string Id => Key.Id;

        public override UserRecord Person => throw new ApiException(StatusCodes.Status403Forbidden, "person_required",
            "This step-up operation requires a person's sign-in access token; project API keys are not allowed.");
    }
}
