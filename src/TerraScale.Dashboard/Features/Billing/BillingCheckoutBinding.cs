using System.Text.Json;
using TerraScale.DotNet.TerraBase;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed partial class StripeBilling
    {
        public const string CheckoutsCollection = "dashboard_stripe_checkouts";

        private static string CheckoutReturnUrl(string checkoutId)
        {
            return Origin + "/billing/checkout/return?session_id=" + Uri.EscapeDataString(checkoutId);
        }

        // Checkout creation and its index have separate CAS writes. An interrupted
        // index write is resumed from the original reservation, never current UI scope.
        private async Task IndexCheckoutAsync(BillingBinding binding, CancellationToken cancellationToken)
        {
            if (binding.CheckoutId.Length == 0 || binding.CustomerId.Length == 0 || !CheckoutReturnPath(binding))
            {
                throw new IOException("Checkout scope is invalid.");
            }

            var value = CheckoutBinding.For(binding);
            try
            {
                await state.PutAsync(CheckoutsCollection, binding.CheckoutId, JsonSerializer.SerializeToElement(value),
                    TerraBasePrecondition.Absent, cancellationToken);
            }
            catch (TerraBasePreconditionFailedException)
            {
                var document = await state.GetAsync(CheckoutsCollection, binding.CheckoutId, cancellationToken);
                if (document is null || document.Id != binding.CheckoutId || AccountService.Read<CheckoutBinding>(document) != value)
                {
                    throw new IOException("Checkout ownership changed.");
                }
            }
        }

        private async Task<BoundCheckout> BoundCheckoutAsync(AccountContext context, string checkoutId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(checkoutId))
            {
                throw new AccountAccessException("Checkout is unavailable.", 404);
            }

            var document = await state.GetAsync(CheckoutsCollection, checkoutId, cancellationToken)
                ?? throw new AccountAccessException("Checkout is unavailable.", 404);
            if (document.Id != checkoutId)
            {
                throw new IOException("Checkout document identity is invalid.");
            }

            var index = AccountService.Read<CheckoutBinding>(document);
            if (index.CheckoutId != checkoutId || string.IsNullOrEmpty(index.OrganizationId) || string.IsNullOrEmpty(index.CustomerId) ||
                string.IsNullOrEmpty(index.RequestId) || index.ProjectId is null || index.ReturnPath is null || index.Plan is null || index.Interval is null)
            {
                throw new IOException("Checkout scope is invalid.");
            }

            // A callback is bound to the original org even when the browser has
            // selected another one. Provider membership is re-read before Stripe IO.
            context = await OwnerAsync(context with { Organization = new OrganizationRecord { Id = index.OrganizationId }, Project = null }, cancellationToken);
            var binding = await ReadAsync(index.OrganizationId, cancellationToken);
            if (binding is null || CheckoutBinding.For(binding) != index || !CheckoutReturnPath(binding))
            {
                throw new AccountAccessException("Checkout is no longer available for this organization.", 404);
            }

            return new(context, binding);
        }

        private async Task RestoreCheckoutScopeAsync(AccountContext context, BillingBinding binding, CancellationToken cancellationToken)
        {
            // Reauthorize after the vendor read and reject a replaced reservation
            // before restoring any account selection.
            var bound = await BoundCheckoutAsync(context, binding.CheckoutId, cancellationToken);
            if (bound.Binding.RequestId != binding.RequestId)
            {
                throw Conflict("Checkout changed concurrently. Reload billing.");
            }

            var organization = bound.Context.Organization!;
            var project = organization.Projects.GetValueOrDefault(binding.CheckoutProjectId);
            if (project?.Status != "active")
            {
                project = organization.Projects.Values.Where(item => item.Status == "active")
                    .OrderBy(item => !item.IsDefault).ThenBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault();
            }

            if (project is not null && project.OrganizationId != organization.Id)
            {
                throw new IOException("Checkout project scope is invalid.");
            }

            await accounts.UpdateAccountAsync(context.Session, account =>
            {
                if (!account.OrganizationIds.Contains(organization.Id))
                {
                    throw new AccountAccessException("Organization is unavailable.", 404);
                }

                account.ActiveOrganizationId = organization.Id;
                account.ActiveProjectId = project?.Id ?? "";
            }, cancellationToken);
        }

        private static bool CheckoutReturnPath(BillingBinding binding)
        {
            return ValidCheckoutReturnPath(binding.OrganizationId, binding.CheckoutProjectId, binding.ReturnPath);
        }

        private sealed record BoundCheckout(AccountContext Context, BillingBinding Binding);
    }

    public sealed record CheckoutBinding(string CheckoutId, string OrganizationId, string CustomerId, string RequestId,
        string ProjectId, string ReturnPath, string Plan, string Interval)
    {
        public static CheckoutBinding For(BillingBinding binding)
        {
            return new(binding.CheckoutId, binding.OrganizationId, binding.CustomerId,
            binding.RequestId, binding.CheckoutProjectId, binding.ReturnPath, binding.Plan, binding.Interval);
        }
    }
}
