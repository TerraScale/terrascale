using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
    [ClassDataSource<DashboardRootFixture>]
    [Category("Slow")]
    public sealed class InternalBillingAuthenticationJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments("report-internal-billing", "Organization has no internal billing policy.")]
        [Arguments("configure-internal-billing", "Internal billing requires the organization named exactly terrascale.")]
        public async Task OriginalCommandEntryRequiresHostedDeviceApprovalAndFreshSignedOperatorBeforeItsTarget(string command, string targetError)
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            var organization = await SignInAsync(browser, producer, "admin");
            var result = await RunApprovedAsync(scenario, producer, CommandArguments(command, organization));
            await ErrorAsync(result, command, targetError);
            await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 0 && result.Observed.GetProperty("issuedAdminRoleGrants").GetInt32() == 2,
                "Browser sign-in and the freshly approved device grant each carry signed operator evidence; the target check fails before any rotation");
            await CleanAsync(scenario, browser, producer);
        }

        [Test]
        [Arguments("ordinary")]
        [Arguments("removed")]
        public async Task OrdinaryOrRemovedPlatformRoleCannotUseTheDeviceGrantAsOperatorAuthority(string state)
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            var organization = await SignInAsync(browser, producer, state == "removed" ? "admin" : "");
            var result = await RunApprovedAsync(scenario, producer, CommandArguments("report-internal-billing", organization),
                beforeDecision: state == "removed" ? () => ControlAsync(producer, new { command = "platform-role", value = "" }) : null);
            await ErrorAsync(result, "report-internal-billing", "Platform administrator authorization is required.");
            await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 0 &&
                result.Observed.GetProperty("issuedAdminRoleGrants").GetInt32() == (state == "removed" ? 1 : 0),
                "The device grant carries the current ordinary or removed role, so the command stops before any privileged rotation");
            await CleanAsync(scenario, browser, producer);
        }

        [Test]
        public async Task AdminEmailArgumentMustMatchTheActualHostedApprovedIdentity()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            var organization = await SignInAsync(browser, producer, "admin");
            var result = await RunApprovedAsync(scenario, producer, CommandArguments("report-internal-billing", organization, "other@example.com"));
            await ErrorAsync(result, "report-internal-billing", "Approved account does not match --admin-email.");
            await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 0, "An email argument does not replace the provider-authenticated principal");
            await CleanAsync(scenario, browser, producer);
        }

        [Test]
        public async Task LostPrivilegedRefreshReplyDoesNotReplayTheDeviceFamilyOrReportAnOperatorSuccess()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            await SignInAsync(browser, producer, "admin");
            // A valid internal target is the first point where the command forces its privileged rotation.
            using var seeded = await producer.PostAsJsonAsync("/__test/control", new { command = "internal-seed", count = 1, paymentMethodValidated = false });
            seeded.EnsureSuccessStatusCode();
            using var targets = JsonDocument.Parse(await seeded.Content.ReadAsStringAsync());
            var organization = targets.RootElement.GetProperty("targets")[0].GetProperty("organizationId").GetString()!;
            var result = await RunApprovedAsync(scenario, producer, CommandArguments("configure-internal-billing", organization),
                beforeDecision: () => ControlAsync(producer, new { command = "drop-refresh-response" }));
            await ErrorAsync(result, "configure-internal-billing", "Internal billing dependencies are unavailable.");
            await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 1, "The real producer spends the single-use rotating grant once despite the lost reply");
            await CleanAsync(scenario, browser, producer);
            await CheckAsync((await ObserveAsync(producer)).GetProperty("refreshes").GetInt32() == 1, "Cleanup never replays the ambiguous rotation");
        }

        [Test]
        public async Task HostedDeviceDenialStopsTheCommandBeforeAnyPrivilegeRefresh()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            var organization = await SignInAsync(browser, producer, "admin");
            var result = await RunApprovedAsync(scenario, producer, CommandArguments("report-internal-billing", organization), approve: false);
            await ErrorAsync(result, "report-internal-billing", "CLI authorization was denied.");
            await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 0 && result.Observed.GetProperty("issuedAdminRoleGrants").GetInt32() == 1,
                "A hosted denial issues no CLI grant and no privilege rotation; only the browser's own grant exists");
            await CleanAsync(scenario, browser, producer, revoked: false);
        }

        [Test]
        public async Task ExpiredHostedDeviceCodeStopsTheCommandWithoutAGrant()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            var organization = await SignInAsync(browser, producer, "admin");
            // ts-auth's clock passes the 600 second device lifetime before anyone decides.
            var result = await RunApprovedAsync(scenario, producer, CommandArguments("report-internal-billing", organization),
                beforeDecision: () => ControlAsync(producer, new { command = "advance", seconds = 601 }), decide: false);
            await ErrorAsync(result, "report-internal-billing", "CLI authorization expired. Start the command again.");
            await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 0 && result.Observed.GetProperty("issuedAdminRoleGrants").GetInt32() == 1,
                "An expired device code issues no CLI grant and no privilege rotation");
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
            await CheckAsync(observed.GetProperty("refreshes").GetInt32() == 0, "The parsing error creates no operator rotation");
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
            if (terminator)
            {
                args = [.. args, "--"];
            }

            var result = await RunApprovedAsync(scenario, producer, args);
            await ErrorAsync(result, command, command == "report-internal-billing"
                ? "Organization has no internal billing policy." : "Internal billing requires the organization named exactly terrascale.");
            await CheckAsync(result.Observed.GetProperty("refreshes").GetInt32() == 0 && result.Observed.GetProperty("issuedAdminRoleGrants").GetInt32() == 2,
                "Valid original flag syntax reaches hosted device approval, the signed operator check and the owning target check");
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
            if (shape == "triple-dash")
            {
                args[1] = "---org";
            }
            else
            {
                args = shape == "trailing-positional" ? [.. args, "--", "unexpected"] : ["report-internal-billing", "--"];
            }

            using var output = new StringWriter(); using var error = new StringWriter();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var exit = await scenario.RunCliAsync(args, output, error, deadline.Token);
            await CheckAsync(exit == 1 && output.ToString() == "" && error.ToString() == "report-internal-billing: " + expected + "\n" && scenario.CliRequests.Count == 0,
                "The real full entry rejects malformed original flag syntax before contacting ts-auth's device endpoint");
            var observed = await ObserveAsync(producer);
            await CheckAsync(observed.GetProperty("refreshes").GetInt32() == 0, "Invalid argv creates no rotating operator family");
            await ProtocolAsync(observed);
        }

        internal static async Task<string> SignInAsync(HttpClient browser, HttpClient producer, string role)
        {
            await ControlAsync(producer, new { command = "platform-role", value = role });
            using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
            await CheckAsync(login.StatusCode == HttpStatusCode.SeeOther, "The operator approval browser signs in through the actual owning producer");
            using var response = await browser.GetAsync("/api/v1/auth/me");
            await CheckAsync(response.StatusCode == HttpStatusCode.OK, "Native identity resolves the provider-backed account and workspace");
            using var identity = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return identity.RootElement.GetProperty("organizationId").GetString()!;
        }

        internal static string[] CommandArguments(string command, string organization, string email = "user@example.com")
        {
            return command == "report-internal-billing"
            ? [command, "--org", organization, "--admin-email", email]
            : [command, "--org", organization, "--admin-email", email, "--base-plan", "pro", "--spend-cap-cents", "840", "--write-cents-per-million", "100", "--storage-cents-per-gb-month", "20"];
        }

        // Reads the printed ts-auth verification URL and code, then decides on the stand-in for ts-auth's hosted page.
        internal static async Task<CommandResult> RunApprovedAsync(AuthProviderScenario scenario, HttpClient producer, string[] args,
            Func<Task>? beforeDecision = null, bool approve = true, bool decide = true, string email = "user@example.com")
        {
            using var output = new PromptWriter(); using var error = new StringWriter();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var running = scenario.RunCliAsync(args, output, error, deadline.Token);
            try
            {
                var raw = await output.Prompt.WaitAsync(deadline.Token);
                var approval = new Uri(raw);
                await CheckAsync(approval.Scheme == "https" && approval.IsDefaultPort && approval.Host == "auth.terrascale.tech" && approval.UserInfo == "" &&
                    approval.AbsolutePath == "/ui/terrascale/device" && approval.Fragment == "" && approval.Query.StartsWith("?user_code=", StringComparison.Ordinal),
                    "The command prints ts-auth's hosted verification page rather than a dashboard or arbitrary origin");
                var code = Uri.UnescapeDataString(approval.Query["?user_code=".Length..]);
                if (beforeDecision is not null)
                {
                    await beforeDecision();
                }

                if (decide)
                {
                    await CheckAsync(scenario.DecideDevice(code, email, approve), "The printed user code names the command's pending ts-auth device grant");
                }

                var exit = await running;
                var text = output.Snapshot;
                await CheckAsync(text.StartsWith("Approve CLI access: " + raw + "\nUser code: " + code + "\n", StringComparison.Ordinal),
                    "The public command prompt contains only the verification URL and its user code");
                await CheckAsync(!(text + error).Contains("protocol-refresh-", StringComparison.Ordinal) && !(text + error).Contains("test-password", StringComparison.Ordinal) &&
                    !(text + error).Contains("device_code", StringComparison.Ordinal) && !(text + error).Contains("sk_management-key", StringComparison.Ordinal),
                    "The complete CLI entry never prints its grant, device secret, password or management credential");
                var requests = scenario.CliRequests;
                var revokes = requests.Count(request => request == "POST /terrascale/oauth/revoke");
                await CheckAsync(requests.Count >= 2 && requests[0] == "POST /terrascale/oauth/device" &&
                    requests.Skip(1).Take(requests.Count - 1 - revokes).All(request => request == "POST /terrascale/oauth/token") &&
                    requests.Skip(requests.Count - revokes).All(request => request == "POST /terrascale/oauth/revoke") && revokes <= 1,
                    "The command starts ts-auth's device grant, polls and rotates on its token endpoint, then revokes at most once");
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
            await CheckAsync(observed.GetProperty("revocations").GetInt32() == (revoked ? 1 : 0) &&
                scenario.CliRequests.Count(request => request == "POST /terrascale/oauth/revoke") == (revoked ? 1 : 0),
                "Only the command's own ts-cli grant is revoked, and only when one was issued");
            using var identity = await browser.GetAsync("/api/v1/auth/me");
            await CheckAsync(identity.StatusCode == HttpStatusCode.OK, "Command cleanup preserves the independently authenticated browser");
            await ProtocolAsync(observed);
        }

        private static async Task ErrorAsync(CommandResult result, string command, string message)
        {
            await CheckAsync(result.ExitCode == 1 && result.Error == command + ": " + message + "\n", "The real public command entry fails with its exact safe owning-boundary error");
        }

        private static async Task ProtocolAsync(JsonElement observed)
        {
            await CheckAsync(observed.GetProperty("contractFailures").GetInt32() == 0 && observed.GetProperty("unsupportedIntrospectionRequests").GetInt32() == 0 &&
                observed.GetProperty("managementRequests").GetInt32() == 0 && observed.GetProperty("managementMutations").GetInt32() == 0,
                "CLI identity uses real userinfo and signed grants without borrowed management authorization or unpublished mutations");
            await CheckAsync(observed.GetProperty("credentialsEncrypted").GetBoolean(),
                "External provider observations confirm no issued grant is stored as plaintext");
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

        internal sealed record CommandResult(int ExitCode, string Output, string Error, JsonElement Observed);

        private sealed class PromptWriter : StringWriter
        {
            private readonly object gate = new();
            private readonly TaskCompletionSource<string> prompt = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<string> Prompt => prompt.Task;
            public string Snapshot { get { lock (gate) { return ToString(); } } }
            public override void WriteLine(string? value)
            {
                lock (gate)
                {
                    base.WriteLine(value);
                    const string prefix = "Approve CLI access: ";
                    if (value?.StartsWith(prefix, StringComparison.Ordinal) == true)
                    {
                        prompt.TrySetResult(value[prefix.Length..]);
                    }
                }
            }
        }
    }
}
