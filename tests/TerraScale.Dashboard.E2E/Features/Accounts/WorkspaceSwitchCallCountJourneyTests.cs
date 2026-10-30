using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.Dashboard.E2E.Features.Accounts
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class WorkspaceSwitchCallCountJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments(false, false)]
        [Arguments(true, false)]
        [Arguments(false, true)]
        [Arguments(true, true)]
        public async Task SwitchLoadsTargetOnceAndRereadsRevokedMembership(bool organizationSwitch, bool mfaRequired)
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            var source = environment.Workspace.Organizations.Values.Single();
            var member = source.Members.Values.Single();
            var target = new OrganizationRecord
            {
                Id = AccountService.NewId(),
                Name = "Target",
                Kind = "business",
                Members = new() { [member.UserId] = member }
            };
            var project = new ProjectRecord { Id = AccountService.NewId(), OrganizationId = target.Id, Name = "Target project", IsDefault = true };
            target.Projects.Add(project.Id, project);
            environment.Workspace.Organizations.Add(target.Id, target);
            await environment.State.PutAsync(AccountService.OrganizationsCollection, target.Id,
                JsonSerializer.SerializeToElement(target), TerraBasePrecondition.Absent, default);
            var platform = DispatchProxy.Create<ITsAuthPlatform, CountingPlatform>();
            var counter = (CountingPlatform)platform;
            counter.Inner = environment.Workspace;
            await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1", builder =>
                builder.Services.Replace(ServiceDescriptor.Singleton(platform)));
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var csrf = await HttpJourney.CsrfAsync(client);
            counter.Calls.Clear();
            counter.Blocked = mfaRequired ? target.Id : "";
            var route = "/api/v1/dashboard/switch-" + (organizationSwitch ? "organization" : "project");
            var form = new Dictionary<string, string>
            {
                ["_csrf"] = csrf,
                [organizationSwitch ? "organizationId" : "projectId"] = organizationSwitch ? target.Id : project.Id,
                ["returnUrl"] = "/databases/" + AccountService.NewId() + "/documents#rows"
            };
            var key = AccountService.AccountStorageKey(member.UserId);
            var beforeSwitch = await environment.State.GetAsync(AccountService.AccountsCollection, key, default);
            using var switched = await HttpJourney.PostFormAsync(client, route, form);
            await Assert.That(counter.Calls[nameof(ITsAuthPlatform.ListOrganizationsAsync)]).IsEqualTo(1);
            await Assert.That(counter.Calls[nameof(ITsAuthPlatform.ListProjectsAsync)]).IsEqualTo(2);
            await Assert.That(counter.Calls.GetValueOrDefault(nameof(ITsAuthPlatform.ListMembersAsync))).IsEqualTo(mfaRequired && !organizationSwitch ? 0 : 1);
            await Assert.That(counter.Calls.GetValueOrDefault(nameof(ITsAuthPlatform.GetOwnershipTransferAsync))).IsEqualTo(mfaRequired && !organizationSwitch ? 0 : 1);
            if (mfaRequired)
            {
                // Stored projects carry no ts-auth organization identity. MFA refusal must not bypass that validation.
                await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
                await Assert.That((await environment.State.GetAsync(AccountService.AccountsCollection, key, default))!.Version).IsEqualTo(beforeSwitch!.Version);
                await app.StopAsync();
                return;
            }
            await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var location = switched.Headers.Location!.OriginalString;
            await Assert.That(location).StartsWith("/databases?");
            await Assert.That(location).Contains("organizationId=" + target.Id);
            await Assert.That(location).Contains("projectId=" + project.Id);
            var selected = AccountService.Read<AccountRecord>((await environment.State.GetAsync(AccountService.AccountsCollection, key, default))!);
            await Assert.That(selected.ActiveOrganizationId).IsEqualTo(target.Id);
            await Assert.That(selected.ActiveProjectId).IsEqualTo(project.Id);
            await Assert.That(selected.OrganizationIds.Contains(target.Id)).IsTrue();
            target.Members.Remove(member.UserId);
            var beforeRefusal = await environment.State.GetAsync(AccountService.AccountsCollection, key, default);
            counter.Calls.Clear();
            using var refused = await HttpJourney.PostFormAsync(client, route, form);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(counter.Calls[nameof(ITsAuthPlatform.ListOrganizationsAsync)]).IsEqualTo(1);
            await Assert.That((await environment.State.GetAsync(AccountService.AccountsCollection, key, default))!.Version).IsEqualTo(beforeRefusal!.Version);
            await app.StopAsync();
        }

        public class CountingPlatform : DispatchProxy
        {
            public ITsAuthPlatform Inner { get; set; } = null!;
            public string Blocked { get; set; } = "";
            public ConcurrentDictionary<string, int> Calls { get; } = new();
            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                Calls.AddOrUpdate(method!.Name, 1, (_, count) => count + 1);
                if (method.Name == nameof(ITsAuthPlatform.ListProjectsAsync) && (string)args![1]! == Blocked)
                {
                    return Task.FromException<IReadOnlyList<TsAuthPlatformProject>>(new AccountAccessException("Two-factor required.", 403, TsAuthGateway.MfaRequiredCode));
                }

                try { return method.Invoke(Inner, args); }
                catch (TargetInvocationException error) when (error.InnerException is not null)
                { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            }
        }
    }
}
