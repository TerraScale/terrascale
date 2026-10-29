using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed record BillingTaxId(string Type, string Value, string Verification);
    public sealed record BillingDetails(string Name, IReadOnlyList<string> Address, IReadOnlyList<BillingTaxId> TaxIds);

    public sealed partial class StripeBilling
    {
        // Stripe owns invoice identity and VAT verification. Never infer an exemption from an entered ID.
        public async Task<BillingDetails?> DetailsAsync(AccountContext context, CancellationToken cancellationToken)
        {
            context = await MemberAsync(context, cancellationToken);
            if (context.Role != "owner" || context.Organization!.OwnerUserId != context.UserId)
            {
                return null;
            }

            var binding = await ReadAsync(context.Organization.Id, cancellationToken);
            if (binding?.CustomerId is not { Length: > 0 })
            {
                return null;
            }

            var index = await state.GetAsync(CustomersCollection, binding.CustomerId, cancellationToken);
            if (index is null || index.Id != binding.CustomerId || AccountService.Read<CustomerBinding>(index).OrganizationId != context.Organization.Id)
            {
                throw new IOException("Billing details customer ownership does not match.");
            }

            var client = await ClientAsync(cancellationToken);
            var customer = await client.V1.Customers.GetAsync(binding.CustomerId,
                new() { Expand = ["tax_ids"] }, cancellationToken: cancellationToken);
            if (customer.Id != binding.CustomerId || customer.Deleted == true || customer.Metadata?.GetValueOrDefault("organization_id") != context.Organization.Id)
            {
                throw new IOException("Billing details customer ownership does not match.");
            }

            var ids = customer.TaxIds;
            if (ids?.Data is null || ids.HasMore || ids.Data.Any(id => id.CustomerId != binding.CustomerId))
            {
                throw new IOException("Billing tax IDs could not be loaded completely for this customer.");
            }

            var address = customer.Address;
            return new(customer.Name ?? "", [.. new[] { address?.Line1, address?.Line2,
                string.Join(" ", new[] { address?.PostalCode, address?.City }.Where(value => !string.IsNullOrWhiteSpace(value))),
                address?.State, address?.Country }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)],
                ids?.Data?.Select(id => new BillingTaxId(id.Type ?? "", id.Value ?? "", id.Verification?.Status ?? "unavailable")).ToArray() ?? []);
        }
    }
}
