using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.PaymentWebhookCorrectness
{
    public sealed class PaymentWebhookAuditJourneyTests
    {
        [Test]
        public async Task ConcurrentReplicasDiscardTheOlderCanonicalSnapshotAfterACasConflict()
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            await CompleteCheckoutAsync(owner, "starter");
            using var first = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            var body = scenario.EventBody();
            var reads = scenario.Stripe.SubscriptionReads;
            scenario.Stripe.Status = "active";
            scenario.Stripe.HoldNextSubscriptionRead = true;
            var stale = PaymentWebhookAuditScenario.SendAsync(first, body);
            try
            {
                await scenario.Stripe.SubscriptionReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
                scenario.Stripe.Status = "canceled";
                using var current = await PaymentWebhookAuditScenario.SendAsync(replica, body, legacy: true);
                await Assert.That(current.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
            finally { scenario.Stripe.ReleaseSubscriptionRead.TrySetResult(); }
            using var recovered = await stale;
            await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(scenario.Stripe.SubscriptionReads - reads).IsEqualTo(3)
                .Because("The losing replica must discard its active snapshot and reread after its billing CAS fails.");
            await Assert.That(scenario.State.Billing!.EffectivePlan).IsEqualTo("free");
            await Assert.That((await OrganizationAsync(owner)).GetProperty("planId").GetString()).IsEqualTo("3BN8V1JQWTB3R3NSZ26SERTHWT");
        }

        [Test]
        public async Task ReplayedAndOutOfOrderEventsUseCurrentStripeFactsAcrossReplicas()
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            await CompleteCheckoutAsync(owner, "starter");
            using var anonymous = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            var replay = scenario.EventBody();
            var revision = scenario.State.Billing!.Revision;
            var reads = scenario.Stripe.SubscriptionReads;
            scenario.Stripe.Status = "past_due";
            using var failed = await PaymentWebhookAuditScenario.SendAsync(anonymous, scenario.EventBody("invoice.payment_failed"));
            await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var dunning = await OrganizationAsync(owner);
            await Assert.That(dunning.GetProperty("planId").GetString()).IsEqualTo("443RCMMF70MNP6S1QMT230NF99");
            await Assert.That(dunning.GetProperty("billingStatus").GetString()).IsEqualTo("billing_required");
            scenario.Stripe.Status = "active";
            using var paid = await PaymentWebhookAuditScenario.SendAsync(replica, scenario.EventBody("invoice.paid"), legacy: true);
            await Assert.That(paid.StatusCode).IsEqualTo(HttpStatusCode.OK);
            scenario.Stripe.Status = "canceled";
            using var newer = await PaymentWebhookAuditScenario.SendAsync(replica, scenario.EventBody("customer.subscription.deleted", "evt_newer", 2), legacy: true);
            await Assert.That(newer.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var older = await PaymentWebhookAuditScenario.SendAsync(anonymous, replay);
            using var duplicate = await PaymentWebhookAuditScenario.SendAsync(replica, replay, legacy: true);
            await Assert.That(older.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var organization = await OrganizationAsync(owner);
            await Assert.That(organization.GetProperty("planId").GetString()).IsEqualTo("3BN8V1JQWTB3R3NSZ26SERTHWT");
            await Assert.That(organization.GetProperty("paymentMethodValidated").GetBoolean()).IsFalse();
            await Assert.That(scenario.State.Billing!.EffectivePlan).IsEqualTo("free");
            await Assert.That(scenario.State.Billing.Revision).IsGreaterThan(revision);
            await Assert.That(scenario.Stripe.SubscriptionReads - reads).IsEqualTo(5)
                .Because("Every delivery, including the duplicate ID, rereads canonical provider state.");
        }

        [Test]
        public async Task InterruptedOrganizationProjectionIsRetryableAndReplayReadsNewCanonicalState()
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            await CompleteCheckoutAsync(owner, "starter");
            using var anonymous = scenario.CreateClient();
            var body = scenario.EventBody("invoice.payment_failed");
            scenario.Stripe.Status = "past_due";
            scenario.State.FailNextOrganizationWrite = true;
            using var interrupted = await PaymentWebhookAuditScenario.SendAsync(anonymous, body);
            await Assert.That(interrupted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(scenario.State.InterruptedWrites).IsEqualTo(1);
            await Assert.That(scenario.State.Billing!.Status).IsEqualTo("past_due");
            var beforeRetry = await OrganizationAsync(owner);
            await Assert.That(beforeRetry.GetProperty("billingStatus").GetString()).IsEqualTo("active")
                .Because("The fault occurs after the billing CAS and before the organization CAS.");
            scenario.Stripe.Status = "canceled";
            using var replica = scenario.CreateClient(replica: true);
            using var retried = await PaymentWebhookAuditScenario.SendAsync(replica, body, legacy: true);
            await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var reconciled = await OrganizationAsync(owner);
            await Assert.That(reconciled.GetProperty("planId").GetString()).IsEqualTo("3BN8V1JQWTB3R3NSZ26SERTHWT");
            await Assert.That(scenario.State.Billing!.Status).IsEqualTo("canceled");
            await Assert.That(scenario.State.Billing.EffectivePlan).IsEqualTo("free");
        }

        [Test]
        public async Task ForeignCurrencyFractionalPricesAndMultipleQuantityInvalidateThePaidBinding()
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            await CompleteCheckoutAsync(owner, "starter");
            using var anonymous = scenario.CreateClient();
            scenario.Stripe.Status = "active";
            foreach (var invalid in new[] { "foreign-currency", "fractional-price", "multiple-quantity" })
            {
                scenario.Stripe.PriceCurrency = invalid == "foreign-currency" ? "eur" : null;
                scenario.Stripe.FractionalPrice = invalid == "fractional-price";
                scenario.Stripe.Quantity = invalid == "multiple-quantity" ? 2 : 1;
                using var delivery = await PaymentWebhookAuditScenario.SendAsync(anonymous, scenario.EventBody(id: "evt_" + invalid));
                await Assert.That(delivery.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var organization = await OrganizationAsync(owner);
                await Assert.That(organization.GetProperty("billingStatus").GetString()).IsEqualTo("billing_required");
                await Assert.That(organization.GetProperty("paymentMethodValidated").GetBoolean()).IsFalse();
                await Assert.That(scenario.State.Billing!.EffectivePlan).IsEqualTo("");
            }
            scenario.Stripe.PriceCurrency = null;
            scenario.Stripe.FractionalPrice = false;
            scenario.Stripe.Quantity = 1;
            using var recovered = await PaymentWebhookAuditScenario.SendAsync(anonymous, scenario.EventBody());
            await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await OrganizationAsync(owner)).GetProperty("billingStatus").GetString()).IsEqualTo("active");
        }

        [Test]
        [Category("PaymentAuditDefect")]
        public async Task UnmappedActivePriceCannotRetainPaidDatabaseCapacity()
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            await CompleteCheckoutAsync(owner, "starter");
            var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=billing");
            using var first = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "payment-audit-first", ["region"] = "GRU" });
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
            scenario.Stripe.Status = "active";
            scenario.Stripe.PriceCurrency = "eur";
            using var anonymous = scenario.CreateClient();
            using var delivery = await PaymentWebhookAuditScenario.SendAsync(anonymous, scenario.EventBody());
            await Assert.That(delivery.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await OrganizationAsync(owner)).GetProperty("billingStatus").GetString()).IsEqualTo("billing_required");
            using var second = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "payment-audit-second", ["region"] = "GRU" });
            await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Conflict)
                .Because("An unqualified Stripe price must not retain Starter's five-database allowance above Personal Free's one database.");
        }

        [Test]
        public async Task RepeatedCheckoutAndCancelKeepProviderKeysAndLateEventsCannotGrantAccess()
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            using var login = await HttpJourney.LoginAsync(owner);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=billing");
            using var first = await CheckoutAsync(owner, csrf, "starter");
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var originalKey = scenario.Stripe.LastCheckoutKey;
            using var repeated = await CheckoutAsync(owner, csrf, "starter");
            await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(repeated.Headers.Location).IsEqualTo(first.Headers.Location);
            var checkoutId = scenario.State.Billing!.CheckoutId;
            using var canceled = await HttpJourney.PostFormAsync(owner, "/checkout/" + checkoutId + "/cancel", new() { ["_csrf"] = csrf });
            await Assert.That(canceled.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var anonymous = scenario.CreateClient();
            using var late = await PaymentWebhookAuditScenario.SendAsync(anonymous, scenario.EventBody("invoice.paid"));
            await Assert.That(late.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(scenario.State.Billing!.PaymentValidated).IsFalse();
            await Assert.That(scenario.State.Billing.SubscriptionId).IsEqualTo("");
            using var replaced = await CheckoutAsync(owner, csrf, "starter");
            await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(scenario.Stripe.LastCheckoutKey).IsNotEqualTo(originalKey);
            await Assert.That(scenario.Stripe.LastTrialDays).IsEqualTo("");
        }

        [Test]
        [Category("PaymentAuditExpectation")]
        public async Task CompletedFreeSetupAllowsImmediatePaidCheckout()
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            await CompleteCheckoutAsync(owner, "free");
            var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=billing");
            using var upgrade = await CheckoutAsync(owner, csrf, "starter");
            await Assert.That(upgrade.StatusCode).IsEqualTo(HttpStatusCode.SeeOther)
                .Because("A completed setup is no longer an unfinished checkout reservation.");
        }

        [Test]
        [Category("PaymentAuditExpectation")]
        public async Task FreeSetupDoesNotInventAPaidSubscriptionTrial()
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            await CompleteCheckoutAsync(owner, "free");
            await Assert.That(scenario.State.Billing).IsNull().Because("Free onboarding must not create a Stripe reservation.");
            var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=billing");
            using var upgrade = await CheckoutAsync(owner, csrf, "starter");
            await Assert.That(upgrade.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(scenario.Stripe.LastTrialDays).IsEqualTo("")
                .Because("The published paid plans do not offer a trial.");
        }

        [Test]
        [Category("PaymentAuditDefect")]
        [Arguments("jpy", 1500L, "JPY 1500")]
        [Arguments("clp", 1234L, "CLP 1234")]
        [Arguments("usd", 501L, "$5.01")]
        public async Task InvoiceHistoryUsesTheCurrencyMinorUnit(string currency, long amount, string expected)
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            await CompleteCheckoutAsync(owner, "starter");
            scenario.Stripe.InvoiceCurrency = currency;
            scenario.Stripe.InvoiceAmount = amount;
            using var history = await owner.GetAsync("/organization?tab=billing");
            await Assert.That(history.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = await history.Content.ReadAsStringAsync();
            var displayed = WebUtility.HtmlDecode(Regex.Match(html, "<td class=\"ts-num\">([^<]+)</td>").Groups[1].Value);
            await Assert.That(displayed).IsEqualTo(expected)
                .Because("The amount returned by Stripe is an integer in this currency's smallest unit.");
        }

        private static Task<HttpResponseMessage> CheckoutAsync(HttpClient owner, string csrf, string plan)
        {
            return HttpJourney.PostFormAsync(owner, "/billing/checkout", new()
            { ["_csrf"] = csrf, ["plan"] = plan, ["interval"] = plan == "starter" ? "annual" : "monthly", ["return"] = "/billing" });
        }

        internal static async Task CompleteCheckoutAsync(HttpClient owner, string plan)
        {
            if (plan == "free")
            {
                const string email = "free-onboarding@example.test";
                using var verified = await HttpJourney.LoginAsync(owner, email, "Free-local-password-123", "/onboarding", signUp: true);
                await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                foreach (var form in new Dictionary<string, string>[]
                {
                    new() { ["step"] = "account-information", ["displayName"] = "Free onboarding" },
                    new() { ["step"] = "payment-method", ["bypass"] = "1" },
                    new() { ["step"] = "project", ["name"] = "Free project", ["region"] = "GRU" }
                })
                {
                    form["_csrf"] = await HttpJourney.CsrfAsync(owner, "/onboarding");
                    using var saved = await HttpJourney.PostFormAsync(owner, "/onboarding/step", form);
                    await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                    await Assert.That(saved.Headers.Location!.OriginalString.Contains("error=", StringComparison.Ordinal)).IsFalse();
                }
                return;
            }
            using var login = await HttpJourney.LoginAsync(owner);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=billing");
            using var checkout = await CheckoutAsync(owner, csrf, plan);
            await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var completed = await owner.GetAsync(checkout.Headers.Location);
            await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.Found);
            using var callback = await owner.GetAsync(completed.Headers.Location);
            await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }

        private static async Task<JsonElement> OrganizationAsync(HttpClient owner)
        {
            using var me = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/auth/me"));
            var id = me.RootElement.GetProperty("organizationId").GetString();
            using var context = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/dashboard/context"));
            return context.RootElement.GetProperty("organizations").EnumerateArray()
                .Single(organization => organization.GetProperty("id").GetString() == id).Clone();
        }
    }
}
