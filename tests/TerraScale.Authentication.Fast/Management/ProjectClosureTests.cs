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
    private static readonly string[] DocumentsRead = ["documents:read"];

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

    private sealed class LegacyErasureContract(HttpStatusCode unsupported, bool nonempty) : DelegatingHandler
    {
        public bool Upgraded { get; set; }
        public bool LoseFirstErasureAnswer { get; set; }
        public bool WroteFence { get; private set; }
        public List<int> Versions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/closure", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                var version = body.GetProperty("version").GetInt32();
                this.Versions.Add(version);
                if (version == 2 && this.LoseFirstErasureAnswer)
                {
                    this.LoseFirstErasureAnswer = false;
                    using var accepted = await base.SendAsync(request, cancellationToken);
                    this.WroteFence = accepted.IsSuccessStatusCode;
                    return new(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("""{"error":{"code":"temporarily_unavailable","message":"answer lost"}}""", Encoding.UTF8, "application/json"),
                    };
                }

                if (!this.Upgraded && (version == 2 || nonempty))
                {
                    // Only the old wire failures are injected; auth persists in real TerraBase and successful closures
                    // run through its real v1/v2 endpoint. A refusal writes no fence.
                    return new(version == 2 ? unsupported : HttpStatusCode.Conflict)
                    {
                        Content = new StringContent(version == 2
                            ? """{"error":{"code":"invalid_request","message":"version must be 1"}}"""
                            : """{"error":{"code":"not_empty","message":"delete what it contains first"}}""", Encoding.UTF8, "application/json"),
                    };
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class LegacyClosureEvidence : DelegatingHandler
    {
        public bool OmitEvidence { get; set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (this.OmitEvidence && request.RequestUri!.AbsolutePath.EndsWith("/closure", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
                body.Remove("evidenceVersion");
                response.Content.Dispose();
                response.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            return response;
        }
    }

    // A wrong listener (404 unknown_route), a wrong peer credential (401) or no TerraBase at all proves nothing: the intent
    // stays, fences the project, and a restarted replica with the right transport finishes it.

    // The account intent is committed before TerraBase is asked; its organization fence keeps a sole organization sole.

    // The other owner of a shared organization leaves while the account closure waits: resuming finds the caller the last
    // owner and ends the account intent. The project closure it had started (pending, maybe fenced in TerraBase) is not
    // rolled back but detached and finished on its own; the account, its organizations, memberships and untouched
    // projects keep every right, and no intent is left behind.

    // One project closure is accepted and removed, the next is refused: the account intent ends, the accepted removal
    // stands, and the refused and not-yet-started projects keep every right with no intent left.

    // QA round 4 (sites cleanup): an accepted account deletion closes its sole organizations, and their project keys kept
    // minting tokens until every region confirmed. Keys stop at acceptance: any pending organization or project closure.

    // Every deferral is a remote-durable write: a pending intent backs off (2 s doubling, at most a minute) instead of being
    // rewritten on every two-second pass while its account intent drives it.

    // Review R2 P2-2: a step that read the organizations before a concurrent cancellation can no longer create an
    // account-owned project intent (its transaction requires the account intent); one created just before is recovered by
    // the background pass, as is a stale organization fence. While the account intent exists they stay its own.

    // Review R2 P3-1 and P3-4: a refusal cancels the intent only at a version that never showed the fence. A refusal read
    // on a stale version is re-read; once a concurrent pass recorded the fence, it stays pending and nothing is cancelled.

    // Review R2 P3-2: once the account closure began removing things, a late last_owner (another owner left a shared
    // organization meanwhile) pauses it instead of cancelling a half-removed account; it finishes once resolved.

    // Review R3-A: a refusal ends the account intent by deleting the parent first. A concurrent pass that deferred the
    // parent (stale version) is read again and the delete retried; one that committed it pauses it instead, leaving the
    // children alone. Two passes, made deterministic by handing the refusing pass the stale parent.

    // Review R3 P3(1) and P3(3): an intent re-created for the same project (same closure id, new generation) is not the one
    // a stale pass read. That pass neither removes the root nor cleans its roles, and reports the closure as pending.

    // Review R3 P3(4): a pending closure refuses writes that add access but lets revocations through: lowering or removing
    // a project role while a project closes, and an owner demoting a member whose account is closing.

    // COS history audit: closure ids are opaque random ULIDs, never derived from the subject, so TerraBase's permanent
    // SYSTEM fence holds no subject hash. A project draws its id once and every later intent (after a refusal included)
    // reuses it, so a fence TerraBase already holds is still recognized; account and organization intents draw their own.

    // Review R4 P3-1: TerraBase showed the fence, but a concurrent refusal had already cancelled the intent. Nothing is in
    // progress, so the answer is 503, not a 202 that promises background work; a retry presents the stored closure id,
    // which TerraBase recognizes, and finishes.

    // Review R4 P3-3: cleaning up a removed account intent's leftovers never touches the intents of a newer account
    // closure for the same user: detaching and fence removal are guarded, in their transaction, by its absence.

    private sealed class HeldClosureCompletion(string id) : DelegatingHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete
                && request.RequestUri!.AbsolutePath.EndsWith("/collections/closure_jobs/documents/by-key", StringComparison.Ordinal)
                && request.RequestUri.Query == "?id=" + id)
            {
                this.Entered.TrySetResult();
                await this.Release.Task.WaitAsync(cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static string Receipt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpRequestMessage BearerRequest(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<(int, string?)> ReceiptCallAsync(HttpClient client, string receipt, HttpMethod? method = null, string? token = null, string? organization = null, OidcHarness? oidc = null)
    {
        var path = "/v1/terrascale/account" + (method == HttpMethod.Delete ? "" : "/deletion") + (organization is null ? "" : "?organization=" + organization);
        using var request = BearerRequest(method ?? HttpMethod.Get, path, token);
        request.Headers.TryAddWithoutValidation("X-TerraScale-Deletion-Receipt", receipt);
        if (oidc is not null)
        {
            await oidc.AttachStepUpAsync(request, token);
        }
        using var response = await client.SendAsync(request);
        return ((int)response.StatusCode, response.IsSuccessStatusCode ? null : await OidcHarness.EnvelopeCodeAsync(response));
    }

    // Only explicit passes run on these hosts. Advance past retry delays between bounded recovery passes;
    // each test checks the resulting persisted state.
    private static async Task SettleAsync(AuthTestServer server, StoreTestClock clock, Func<Task<bool>> done)
    {
        var worker = server.App.Services.GetRequiredService<ClosureWorker>();
        for (var pass = 0; pass < 5 && !await done(); pass++)
        {
            await worker.RunOnceAsync();
            clock.UtcNow = clock.UtcNow.AddMinutes(2);
        }
    }

    private void Misroute(IServiceCollection services, string fault) =>
        services.AddKeyedSingleton(App.StorageAuthority, (_, _) => fault switch
        {
            // The public listener does not serve the peer operation.
            "listener" => new TerraBaseManagementClient(fixture.Server.Http, new TerraBaseManagementClientOptions { BaseAddress = fixture.Server.ClientAddress }),
            // The internal listener without the peer credential.
            "credential" => new TerraBaseManagementClient(TerraBaseFixture.NewHttpClient(), new TerraBaseManagementClientOptions { BaseAddress = fixture.Server.InternalAddress }),
            _ => new TerraBaseManagementClient(TerraBaseFixture.NewHttpClient(), new TerraBaseManagementClientOptions { BaseAddress = new Uri("http://127.0.0.1:1") }),
        });

    // TerraBase holds a permanent fence for the project under some closure id: any other one is refused.
    private async Task FencedAsync(string project, string organization)
    {
        var refused = await Assert.That(async () => await fixture.Server.Management().CloseProjectAsync(project, organization, AuthStore.NewId())).Throws<TerraBaseConflictException>();
        await Assert.That(refused!.ErrorCode).IsEqualTo("closure_conflict");
    }

    private static async Task<ClosureJob?> JobAsync(OidcHarness oidc, string scope, string subject) =>
        (await oidc.Server.App.Services.GetRequiredService<AuthStore>().GetAsync(AuthStore.ClosureJobCollection, "terrascale", ClosureJob.Key(scope, subject)))
            ?.Document.Deserialize(ClosureJson.Default.ClosureJob);
}
