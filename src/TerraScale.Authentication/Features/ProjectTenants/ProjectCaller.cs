using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;

namespace TerraScale.Authentication.Features.ProjectTenants;

public readonly union PersonResult(UserRecord, Forbidden);

public abstract record ProjectCaller
{
    private ProjectCaller() { }

    public abstract string Id { get; }

    public PersonResult RequirePerson() => this is SignedIn(var user) ? user : PersonRequired;

    public static readonly Forbidden PersonRequired = new(
        "This operation requires a person's sign-in access token; project API keys are not allowed.", "person_required");

    public sealed record SignedIn(UserRecord User) : ProjectCaller
    {
        public override string Id => User.Id;
    }

    public sealed record SecretKey(ApiKeyRecord Key) : ProjectCaller
    {
        public override string Id => Key.Id;
    }
}
