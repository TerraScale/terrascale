using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
[Category("Slow")]
public sealed class InternalBillingAuthenticationJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    [Arguments("report-internal-billing", "Organization has no internal billing policy.")]
    [Arguments("configure-internal-billing", "Internal billing requires the organization named exactly terrascale.")]
    public async Task OriginalCommandEntryRequiresNativeDeviceApprovalAndFreshSignedOperatorBeforeItsTarget(string command, string targetError)
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var organization = await SignInAsync(browser, producer, "admin");
        var result = await RunApprovedAsync(scenario, browser, producer, CommandArguments(command, organization));
        await ErrorAsync(result, command, targetError);
        await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 1 && result.Observed.GetProperty("issuedAdminRoleGrants").GetInt32() == 3,
            "Browser sign-in, independent device reauthentication and fresh persisted command rotation each receive actual signed operator evidence");
        await CleanAsync(scenario, browser, producer);
    }

    [Test]
    [Arguments("ordinary")]
    [Arguments("removed")]
    public async Task OrdinaryOrRemovedPlatformRoleCannotUseADevicePatAsOperatorAuthority(string state)
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var organization = await SignInAsync(browser, producer, state == "removed" ? "admin" : "");
        var result = await RunApprovedAsync(scenario, browser, producer, CommandArguments("report-internal-billing", organization),
            beforePoll: state == "removed" ? () => ControlAsync(producer, new { command = "platform-role", value = "" }) : null);
        await ErrorAsync(result, "report-internal-billing", "Platform administrator authorization is required.");
        await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 1 &&
            result.Observed.GetProperty("issuedAdminRoleGrants").GetInt32() == (state == "removed" ? 2 : 0),
            "The command rotates its independent PAT family and reads the removed or ordinary role from a newly signed access grant");
        await CleanAsync(scenario, browser, producer);
    }

    [Test]
    public async Task AdminEmailArgumentMustMatchTheActualNativelyApprovedIdentity()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var organization = await SignInAsync(browser, producer, "admin");
        var result = await RunApprovedAsync(scenario, browser, producer, CommandArguments("report-internal-billing", organization, "other@example.com"));
        await ErrorAsync(result, "report-internal-billing", "Approved account does not match --admin-email.");
        await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 1, "An email argument does not replace the fresh provider-authenticated principal");
        await CleanAsync(scenario, browser, producer);
    }

    [Test]
    public async Task LostPrivilegedRefreshReplyDoesNotReplayTheDeviceFamilyOrReportAnOperatorSuccess()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var organization = await SignInAsync(browser, producer, "admin");
        var result = await RunApprovedAsync(scenario, browser, producer, CommandArguments("report-internal-billing", organization),
            beforePoll: () => ControlAsync(producer, new { command = "drop-refresh-response" }));
        await ErrorAsync(result, "report-internal-billing", "Internal billing dependencies are unavailable.");
        await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 1, "The real producer spends the single-use rotating grant once despite the lost reply");
        await CleanAsync(scenario, browser, producer);
        await CheckAsync((await ObserveAsync(producer)).GetProperty("refreshes").GetInt32() == 1, "Native cleanup never replays the ambiguous rotation");
    }

    [Test]
    public async Task NativeDeviceDenialStopsTheCommandBeforeAnyPrivilegeRefresh()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var organization = await SignInAsync(browser, producer, "admin");
        var result = await RunApprovedAsync(scenario, browser, producer, CommandArguments("report-internal-billing", organization), decision: "deny");
        await ErrorAsync(result, "report-internal-billing", "CLI authorization was denied.");
        await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 0 && result.Observed.GetProperty("passwordGrants").GetInt32() == 1,
            "A native denial neither reauthenticates the browser nor creates a privilege grant");
        await CleanAsync(scenario, browser, producer, revoked: false);
    }

    [Test]
    [Arguments("9223372036854775808")]
    [Arguments("NaN")]
    [Arguments("08")]
    [Arguments("0b102")]
    [Arguments("0x__64")]
    [Arguments("100_")]
    [Arguments("1__0")]
    [Arguments("0x8000000000000000")]
    public async Task OriginalConfigureArgumentsRejectMalformedRatesBeforeStartingDeviceAuthorization(string rate)
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient();
        using var browser = scenario.CreateClient();
        var organization = await SignInAsync(browser, producer, "admin");
        var args = CommandArguments("configure-internal-billing", organization);
        args[Array.IndexOf(args, "--write-cents-per-million") + 1] = rate;
        using var output = new StringWriter(); using var error = new StringWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exit = await scenario.RunCliAsync(args, output, error, deadline.Token);
        await CheckAsync(exit == 1 && output.ToString() == "" && error.ToString() == "configure-internal-billing: --write-cents-per-million requires a signed 64-bit integer.\n",
            "The actual public command entry reports the original numeric argument error without issuing an approval code");
        await CheckAsync(scenario.CliRequests.Count == 0, "Invalid command arguments do not contact the running device endpoint");
        var observed = await ObserveAsync(producer);
        await CheckAsync(observed.GetProperty("refreshes").GetInt32() == 0 && observed.GetProperty("encryptedCredentialRecords").GetInt32() == 0,
            "The parsing error creates no encrypted credential or operator rotation");
        await ProtocolAsync(observed);
    }

    [Test]
    [Arguments("report-internal-billing", "-", false)]
    [Arguments("configure-internal-billing", "-", false)]
    [Arguments("report-internal-billing", "--", true)]
    [Arguments("configure-internal-billing", "--", true)]
    public async Task OriginalSingleDashFlagsAndTerminalDoubleDashRunTheWholeNativeCommand(string command, string prefix, bool terminator)
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var organization = await SignInAsync(browser, producer, "admin");
        var args = CommandArguments(command, organization).Select(argument => argument.StartsWith("--", StringComparison.Ordinal)
            ? prefix + argument[2..] : argument).ToArray();
        if (terminator) args = [.. args, "--"];
        var result = await RunApprovedAsync(scenario, browser, producer, args);
        await ErrorAsync(result, command, command == "report-internal-billing"
            ? "Organization has no internal billing policy." : "Internal billing requires the organization named exactly terrascale.");
        await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 1,
            "Valid original flag syntax reaches actual device approval, signed current operator rotation and the owning target check");
        await CleanAsync(scenario, browser, producer);
    }

    [Test]
    [Arguments("triple-dash", "Invalid flag syntax: ---org.")]
    [Arguments("trailing-positional", "Unexpected positional arguments.")]
    [Arguments("early-terminator", "--org is required.")]
    public async Task MalformedDashOrTerminatorArgumentsNeverIssueADeviceGrant(string shape, string expected)
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var producer = scenario.CreateProviderClient(); using var browser = scenario.CreateClient();
        var organization = await SignInAsync(browser, producer, "admin");
        var args = CommandArguments("report-internal-billing", organization);
        if (shape == "triple-dash") args[1] = "---org";
        else if (shape == "trailing-positional") args = [.. args, "--", "unexpected"];
        else args = ["report-internal-billing", "--"];
        using var output = new StringWriter(); using var error = new StringWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exit = await scenario.RunCliAsync(args, output, error, deadline.Token);
        await CheckAsync(exit == 1 && output.ToString() == "" && error.ToString() == "report-internal-billing: " + expected + "\n" && scenario.CliRequests.Count == 0,
            "The real full entry rejects malformed original flag syntax before native device issue");
        var observed = await ObserveAsync(producer);
        await CheckAsync(observed.GetProperty("refreshes").GetInt32() == 0 && observed.GetProperty("encryptedCredentialRecords").GetInt32() == 0 &&
            observed.GetProperty("accessTokenHashRecords").GetInt32() == 0,
            "Invalid argv creates no encrypted operator credential, rotating family or personal token index");
        await ProtocolAsync(observed);
    }

    private static async Task<string> SignInAsync(HttpClient browser, HttpClient producer, string role)
    {
        await ControlAsync(producer, new { command = "platform-role", value = role });
        using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
        await CheckAsync(login.StatusCode == HttpStatusCode.SeeOther, "The operator approval browser signs in through the actual owning producer");
        using var response = await browser.GetAsync("/api/v1/auth/me");
        await CheckAsync(response.StatusCode == HttpStatusCode.OK, "Native identity resolves the provider-backed account and workspace");
        using var identity = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return identity.RootElement.GetProperty("organizationId").GetString()!;
    }

    private static string[] CommandArguments(string command, string organization, string email = "user@example.com") => command == "report-internal-billing"
        ? [command, "--org", organization, "--admin-email", email]
        : [command, "--org", organization, "--admin-email", email, "--base-plan", "business", "--spend-cap-cents", "840", "--write-cents-per-million", "100", "--storage-cents-per-gib-month", "20"];

    private static async Task<CommandResult> RunApprovedAsync(AuthProviderScenario scenario, HttpClient browser, HttpClient producer, string[] args,
        Func<Task>? beforePoll = null, string decision = "approve")
    {
        using var output = new PromptWriter(); using var error = new StringWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var running = scenario.RunCliAsync(args, output, error, deadline.Token);
        try
        {
            var raw = await output.Prompt.WaitAsync(deadline.Token);
            var approval = new Uri(raw);
            await CheckAsync(approval.GetLeftPart(UriPartial.Authority) == scenario.Origin.GetLeftPart(UriPartial.Authority) && approval.AbsolutePath == "/cli/approve" && approval.Fragment == "",
                "The command prints the actual native approval origin rather than an arbitrary external redirect");
            var code = Uri.UnescapeDataString(approval.Query["?user_code=".Length..]);
            await CheckAsync(Regex.IsMatch(code, "^[A-Z2-9]{4}-[A-Z2-9]{4}$", RegexOptions.CultureInvariant), "The displayed public user code is the native device code format");
            var csrf = await HttpJourney.CsrfAsync(browser, approval.PathAndQuery);
            using var accepted = await HttpJourney.PostFormAsync(browser, "/cli/approve/submit", new()
            { ["_csrf"] = csrf, ["user_code"] = code, ["decision"] = decision, ["password"] = decision == "approve" ? "test-password" : "" });
            await CheckAsync(accepted.StatusCode == HttpStatusCode.OK, "The actual native form decides the command's pending device grant");
            await CheckAsync((await accepted.Content.ReadAsStringAsync()).Contains(decision == "approve" ? "CLI authorized" : "Request denied", StringComparison.Ordinal),
                "Native approval renders the owning device decision");
            if (beforePoll is not null) await beforePoll();
            // Hold the scenario clock until approval and any authority change
            // finish. Early real-time polls cannot redeem before this boundary.
            await ControlAsync(producer, new { command = "advance", seconds = 60 });
            var exit = await running;
            var text = output.Snapshot;
            await CheckAsync(text == "Approve CLI access: " + approval.AbsoluteUri + "\nUser code: " + code + "\n",
                "The public command prompt contains only the verification URL and public user code");
            await CheckAsync(!(text + error).Contains("tspat_", StringComparison.Ordinal) && !(text + error).Contains("test-password", StringComparison.Ordinal) &&
                !(text + error).Contains("device_code", StringComparison.Ordinal) && !(text + error).Contains("sk_management-key", StringComparison.Ordinal),
                "The complete CLI entry never prints its PAT, device secret, password or management credential");
            await CheckAsync(scenario.CliRequests.Count >= 2 && scenario.CliRequests[0] == "POST /api/v1/dashboard/cli/device-authorization" &&
                scenario.CliRequests.Skip(1).All(request => request == "POST /api/v1/dashboard/cli/token"),
                "The command issues and polls through real HTTP application endpoints");
            return new(exit, text, error.ToString(), await ObserveAsync(producer));
        }
        finally
        {
            if (!running.IsCompleted)
            {
                await deadline.CancelAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(35));
            }
        }
    }

    private static async Task CleanAsync(AuthProviderScenario scenario, HttpClient browser, HttpClient producer, bool revoked = true)
    {
        var observed = await ObserveAsync(producer);
        await CheckAsync(observed.GetProperty("encryptedCredentialRecords").GetInt32() == 0 && observed.GetProperty("accessTokenHashRecords").GetInt32() == 0,
            "The command removes its encrypted credential and hashed PAT index after its actual result");
        await CheckAsync(observed.GetProperty("revocations").GetInt32() == (revoked ? 1 : 0), "Only the command's independent provider family is revoked");
        using var profile = await browser.GetAsync("/profile?tab=access-tokens");
        await CheckAsync(profile.StatusCode == HttpStatusCode.OK && (await profile.Content.ReadAsStringAsync()).Contains("No access tokens", StringComparison.Ordinal),
            "Native profile shows no forgotten command PAT while the ordinary browser remains signed in");
        using var identity = await browser.GetAsync("/api/v1/auth/me");
        await CheckAsync(identity.StatusCode == HttpStatusCode.OK, "Command cleanup preserves the independently authenticated browser");
        await ProtocolAsync(observed);
    }

    private static async Task ErrorAsync(CommandResult result, string command, string message) =>
        await CheckAsync(result.ExitCode == 1 && result.Error == command + ": " + message + "\n", "The real public command entry fails with its exact safe owning-boundary error");

    private static async Task ProtocolAsync(JsonElement observed)
    {
        await CheckAsync(observed.GetProperty("contractFailures").GetInt32() == 0 && observed.GetProperty("unsupportedIntrospectionRequests").GetInt32() == 0 &&
            observed.GetProperty("managementRequests").GetInt32() == 0 && observed.GetProperty("managementMutations").GetInt32() == 0,
            "CLI identity uses real userinfo and signed grants without borrowed management authorization or unpublished mutations");
        await CheckAsync(observed.GetProperty("credentialsEncrypted").GetBoolean() && observed.GetProperty("enrollmentSecretsEncrypted").GetBoolean(),
            "External provider observations confirm authentication material is never stored as plaintext");
    }

    private static async Task ControlAsync(HttpClient producer, object command)
    {
        using var response = await producer.PostAsJsonAsync("/__test/control", command);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> ObserveAsync(HttpClient producer)
    {
        using var response = await producer.GetAsync("/__test/observations");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static async Task CheckAsync(bool condition, string message)
    {
        await Assert.That(condition).IsTrue().Because(message);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error, JsonElement Observed);

    private sealed class PromptWriter : StringWriter
    {
        private readonly object gate = new();
        private readonly TaskCompletionSource<string> prompt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> Prompt => prompt.Task;
        public string Snapshot { get { lock (gate) return ToString(); } }
        public override void WriteLine(string? value)
        {
            lock (gate)
            {
                base.WriteLine(value);
                const string prefix = "Approve CLI access: ";
                if (value?.StartsWith(prefix, StringComparison.Ordinal) == true) prompt.TrySetResult(value[prefix.Length..]);
            }
        }
    }
}
