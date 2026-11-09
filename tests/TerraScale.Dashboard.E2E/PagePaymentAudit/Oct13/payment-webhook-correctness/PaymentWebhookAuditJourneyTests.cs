using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.PaymentWebhookCorrectness
{
    public sealed class PaymentWebhookAuditJourneyTests
    {
        [Test]
        public async Task ReplayedAndOutOfOrderEventsUseCurrentStripeFactsAcrossReplicas()
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            await CompleteCheckoutAsync(owner, "starter");
            using var anonymous = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            var replay = await scenario.EventBodyAsync();
            var revision = (await scenario.BillingAsync()).Revision;
            var reads = scenario.Stripe.SubscriptionReads;
            scenario.Stripe.Status = "past_due";
            using var failed = await PaymentWebhookAuditScenario.SendAsync(anonymous, await scenario.EventBodyAsync("invoice.payment_failed"));
            await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var dunning = await OrganizationAsync(owner);
            await Assert.That(dunning.GetProperty("planId").GetString()).IsEqualTo("443RCMMF70MNP6S1QMT230NF99");
            await Assert.That(dunning.GetProperty("billingStatus").GetString()).IsEqualTo("billing_required");
            scenario.Stripe.Status = "active";
            using var paid = await PaymentWebhookAuditScenario.SendAsync(replica, await scenario.EventBodyAsync("invoice.paid"), legacy: true);
            await Assert.That(paid.StatusCode).IsEqualTo(HttpStatusCode.OK);
            scenario.Stripe.Status = "canceled";
            using var newer = await PaymentWebhookAuditScenario.SendAsync(replica, await scenario.EventBodyAsync("customer.subscription.deleted", "evt_newer", 2), legacy: true);
            await Assert.That(newer.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var older = await PaymentWebhookAuditScenario.SendAsync(anonymous, replay);
            using var duplicate = await PaymentWebhookAuditScenario.SendAsync(replica, replay, legacy: true);
            await Assert.That(older.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var organization = await OrganizationAsync(owner);
            await Assert.That(organization.GetProperty("planId").GetString()).IsEqualTo("3BN8V1JQWTB3R3NSZ26SERTHWT");
            await Assert.That(organization.GetProperty("paymentMethodValidated").GetBoolean()).IsFalse();
            var billing = await scenario.BillingAsync();
            await Assert.That(billing.EffectivePlan).IsEqualTo("free");
            await Assert.That(billing.Revision).IsGreaterThan(revision);
            await Assert.That(scenario.Stripe.SubscriptionReads - reads).IsEqualTo(5)
                .Because("Every delivery, including the duplicate ID, rereads canonical provider state.");
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
            { ["_csrf"] = csrf, ["name"] = "payment-audit-first", ["homeRegion"] = "GRU" });
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
            scenario.Stripe.Status = "active";
            scenario.Stripe.PriceCurrency = "eur";
            using var anonymous = scenario.CreateClient();
            using var delivery = await PaymentWebhookAuditScenario.SendAsync(anonymous, await scenario.EventBodyAsync());
            await Assert.That(delivery.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await OrganizationAsync(owner)).GetProperty("billingStatus").GetString()).IsEqualTo("billing_required");
            using var second = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "payment-audit-second", ["homeRegion"] = "GRU" });
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
            var checkoutId = (await scenario.BillingAsync()).CheckoutId;
            using var canceled = await HttpJourney.PostFormAsync(owner, "/checkout/" + checkoutId + "/cancel", new() { ["_csrf"] = csrf });
            await Assert.That(canceled.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var anonymous = scenario.CreateClient();
            using var late = await PaymentWebhookAuditScenario.SendAsync(anonymous, await scenario.EventBodyAsync("invoice.paid"));
            await Assert.That(late.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var billing = await scenario.BillingAsync();
            await Assert.That((await OrganizationAsync(owner)).GetProperty("paymentMethodValidated").GetBoolean()).IsFalse();
            await Assert.That(billing.SubscriptionId).IsEqualTo("");
            using var replaced = await CheckoutAsync(owner, csrf, "starter");
            await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(scenario.Stripe.LastCheckoutKey).IsNotEqualTo(originalKey);
            await Assert.That(scenario.Stripe.LastTrialDays).IsEqualTo("");
        }

        [Test]
        public async Task WebhookRacingAUserRefreshOnAnotherReplicaRereadsAndKeepsBothWrites()
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            await CompleteCheckoutAsync(owner, "starter");
            var before = await scenario.BillingAsync();
            using var anonymous = scenario.CreateClient(replica: true);
            var body = await scenario.EventBodyAsync();
            scenario.Stripe.HoldNextSubscriptionRead = true;
            var webhook = PaymentWebhookAuditScenario.SendAsync(anonymous, body);
            await scenario.Stripe.SubscriptionReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (var callback = await owner.GetAsync("/billing/checkout/return?session_id=" + Uri.EscapeDataString(before.CheckoutId)))
            {
                await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var user = await scenario.BillingAsync();
            await Assert.That(user.Revision).IsEqualTo(before.Revision + 1);
            scenario.Stripe.ReleaseSubscriptionRead.TrySetResult();
            using var delivered = await webhook;
            await Assert.That(delivered.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var after = await scenario.BillingAsync();
            await Assert.That(after.Revision).IsEqualTo(before.Revision + 2)
                .Because("The webhook read the row before the user's write, lost the version check, reread it and applied on top.");
            await Assert.That(after.Version).IsNotEqualTo(user.Version);
            var organization = await OrganizationAsync(owner);
            await Assert.That(organization.GetProperty("paymentMethodValidated").GetBoolean()).IsTrue();
            await Assert.That(organization.GetProperty("billingStatus").GetString()).IsEqualTo("active");
        }

        [Test]
        public async Task SecondStripeCustomerForAnOrganizationIsRejected()
        {
            await using var scenario = await PaymentWebhookAuditScenario.StartAsync();
            using var owner = scenario.CreateClient();
            await CompleteCheckoutAsync(owner, "starter");
            var binding = await scenario.BillingAsync();
            await using (var db = await scenario.StorageAsync())
            {
                db.StripeCustomers.Add(new StripeCustomerRow
                {
                    Id = AccountService.NewId(),
                    CustomerId = "cus_payment_audit_duplicate",
                    OrganizationId = binding.OrganizationId,
                    CreatedAt = DateTime.UtcNow,
                });
                var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                await Assert.That(error!.InnerException is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.UniqueViolation }).IsTrue();
            }

            await using (var db = await scenario.StorageAsync())
            {
                var customers = await db.StripeCustomers.AsNoTracking().Where(row => row.OrganizationId == binding.OrganizationId).Select(row => row.CustomerId).ToListAsync();
                await Assert.That(customers).IsEquivalentTo([binding.CustomerId]);
            }
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
                    new() { ["step"] = "payment-method", ["plan"] = "free" },
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
