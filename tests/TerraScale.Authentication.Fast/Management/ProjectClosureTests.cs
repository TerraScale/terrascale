using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>
/// Project and account closure against a real TerraBase over ts-auth's peer transport and the ts-dotnet SDK
/// (docs/terrabase/project-closure.md): no TerraBase answer other than acceptance removes an auth root.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProjectClosureTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_project_is_removed_only_after_terrabase_accepts_and_a_lost_answer_is_retried_with_the_same_closure()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false);
        var (owner, token) = await oidc.SignInUserAsync("owner@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var first = await oidc.CreateProjectAsync(token, organization, "First");
        var second = await oidc.CreateProjectAsync(token, organization, "Second");
        var terraBase = fixture.Server.Management();

        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{first}", token, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{organization}/projects/{first}", token)).IsEqualTo((404, "not_found"));
        await FencedAsync(first, organization);

        // The intent is committed and TerraBase accepted, then ts-auth lost the answer before removing the root: the retry
        // presents the same stored closure.
        var intent = await oidc.Server.App.Services.GetRequiredService<PlatformDirectory>().RequestProjectClosureAsync(owner.Id, organization, second, null, CancellationToken.None);
        await Assert.That(intent.Value.ProjectClosureVersion).IsEqualTo(1);
        await Assert.That((await terraBase.CloseProjectAsync(second, organization, intent.Value.Id)).Accepted).IsTrue();
        await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{organization}/projects/{second}", token);
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{second}", token, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{organization}/projects/{second}", token)).IsEqualTo((404, "not_found"));
        await Assert.That(await JobAsync(oidc, "project", second)).IsNull();
    }

    // TerraBase retains the project fence and refuses a different closure id.
    private async Task FencedAsync(string project, string organization)
    {
        var refused = await Assert.That(async () => await fixture.Server.Management().CloseProjectAsync(project, organization, AuthStore.NewId())).Throws<TerraBaseConflictException>();
        await Assert.That(refused!.ErrorCode).IsEqualTo("closure_conflict");
    }

    private static async Task<ClosureJob?> JobAsync(OidcHarness oidc, string scope, string subject) =>
        (await oidc.Server.App.Services.GetRequiredService<AuthStore>().GetAsync(AuthStore.ClosureJobCollection, "terrascale", ClosureJob.Key(scope, subject)))
            ?.Document.Deserialize(ClosureJson.Default.ClosureJob);
}
