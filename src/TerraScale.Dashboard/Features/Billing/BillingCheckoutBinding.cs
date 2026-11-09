using Microsoft.EntityFrameworkCore;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed partial class StripeBilling
    {
        private static string CheckoutReturnUrl(string checkoutId)
        {
            return Origin + "/billing/checkout/return?session_id=" + Uri.EscapeDataString(checkoutId);
        }

        private async Task IndexCheckoutAsync(BillingBinding binding, CancellationToken cancellationToken)
        {
            if (binding.CheckoutId.Length == 0 || binding.CustomerId.Length == 0 || !CheckoutReturnPath(binding))
            {
                throw new IOException("Checkout scope is invalid.");
            }

            var value = CheckoutBinding.For(binding);
            var row = new StripeCheckoutRow
            {
                Id = AccountService.NewId(),
                CheckoutId = value.CheckoutId,
                OrganizationId = value.OrganizationId,
                CustomerId = value.CustomerId,
                RequestId = value.RequestId,
                ProjectId = value.ProjectId,
                ReturnPath = value.ReturnPath,
                Plan = value.Plan,
                Interval = value.Interval,
                CreatedAt = time.GetUtcNow().UtcDateTime,
            };
            if (!await storage.InsertAsync(row, cancellationToken) && (await CheckoutAsync(binding.CheckoutId, cancellationToken))?.Binding != value)
            {
                throw new IOException("Checkout ownership changed.");
            }
        }

        private async Task<BoundCheckout> BoundCheckoutAsync(AccountContext context, string checkoutId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(checkoutId))
            {
                throw new AccountAccessException("Checkout is unavailable.", 404);
            }

            var index = (await CheckoutAsync(checkoutId, cancellationToken) ?? throw new AccountAccessException("Checkout is unavailable.", 404)).Binding;
            context = await OwnerAsync(context with { Organization = new OrganizationRecord { Id = index.OrganizationId }, Project = null }, cancellationToken);
            var binding = await ReadAsync(index.OrganizationId, cancellationToken);
            if (binding is null || CheckoutBinding.For(binding) != index || !CheckoutReturnPath(binding))
            {
                throw new AccountAccessException("Checkout is no longer available for this organization.", 404);
            }

            return new(context, binding);
        }

        private async Task<StripeCheckoutRow?> CheckoutAsync(string checkoutId, CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            return await db.StripeCheckouts.AsNoTracking().SingleOrDefaultAsync(row => row.CheckoutId == checkoutId, cancellationToken);
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
