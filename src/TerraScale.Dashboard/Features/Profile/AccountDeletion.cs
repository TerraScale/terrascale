using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Profile;

// ts-auth deletes the user, its sessions, tokens, memberships and the organizations only
// the user belongs to; the dashboard then drops its own records for the same scope.
public static class AccountDeletion
{
    public static async Task<IResult> DeleteAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ITsAuthPlatform platform, IDashboardState state, AccessTokens tokens, StripeBilling billing)
    {
        var context = await AccountRequest.RequireAsync(http, sessions, accounts);
        if (context is null) return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
        var cancellationToken = http.RequestAborted;
        List<string> sole = [], ending = [];
        try
        {
            var typed = form["email"].ToString().Trim();
            if (typed.Length == 0 || !typed.Equals(context.Account.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new AccountAccessException("Type your account email to confirm deletion.", 422);
            foreach (var org in context.Organizations)
            {
                var members = await platform.ListMembersAsync(context.Session, org.Id, cancellationToken);
                if (members.Any(member => member.UserId != context.UserId))
                {
                    // ts-auth refuses this too, but only after the subscriptions below were ended.
                    if (members.Any(member => member.UserId == context.UserId && member.Role == "owner") && !members.Any(member => member.UserId != context.UserId && member.Role == "owner"))
                        throw new AccountAccessException("Transfer ownership of your shared organizations before deleting your account.", 409);
                    continue;
                }
                if (org.Projects.Values.Any(project => project.Databases.Count > 0))
                    throw new AccountAccessException($"Delete your databases in {org.Name} before deleting your account.", 409);
                var (end, subscriptionId) = await billing.SubscriptionEndAsync(org.Id, cancellationToken);
                if (end == SubscriptionEnd.Renews)
                    throw new AccountAccessException($"Switch {org.Name} to the Free plan in Billing before deleting your account.", 409);
                if (end == SubscriptionEnd.AtPeriodEnd) ending.Add(subscriptionId);
                sole.Add(org.Id);
            }
            // Every check passed; only now end the subscriptions the customer already set to end.
            foreach (var subscriptionId in ending) await billing.EndNowAsync(subscriptionId, cancellationToken);
            try { await platform.DeleteAccountAsync(context.Session, cancellationToken); }
            catch (AccountAccessException error) when (error.Code == "last_owner")
            { throw new AccountAccessException("Transfer ownership of your shared organizations before deleting your account.", 409); }
            // Dashboard records go only after ts-auth confirms; a 401/404 can mean an expired token or a ts-auth without the route.
        }
        catch (Exception error) when (error is AccountAccessException or Stripe.StripeException)
        {
            var view = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), "danger",
                Error: error is AccountAccessException ? error.Message : "Stripe could not update your plan. Try again.",
                Tokens: await tokens.ListAsync(context, cancellationToken), Sessions: await sessions.ListAsync(http, context.Session)));
            view.StatusCode = error is AccountAccessException access ? access.StatusCode : 502;
            return view;
        }
        foreach (var orgId in sole)
        {
            if (await state.GetAsync(StripeBilling.Collection, orgId, cancellationToken) is { } binding &&
                StripeBilling.ReadBinding(binding, orgId).CustomerId is { Length: > 0 } customer)
                await DropAsync(state, StripeBilling.CustomersCollection, customer, cancellationToken);
            await DropAsync(state, StripeBilling.Collection, orgId, cancellationToken);
            await DropAsync(state, AccountService.OrganizationsCollection, orgId, cancellationToken);
        }
        await tokens.ForgetAllAsync(context.UserId, cancellationToken);
        var key = AccountService.AccountStorageKey(context.UserId);
        await DropAsync(state, AccountService.AccountsCollection, key, cancellationToken);
        await DropAsync(state, AccountService.IdentitiesCollection, key, cancellationToken);
        await sessions.RevokeUserAsync(context.UserId, cancellationToken);
        await sessions.EndAsync(http);
        return HttpRedirects.SeeOther("/login");
    }

    private static async Task DropAsync(IDashboardState state, string collection, string id, CancellationToken cancellationToken)
    {
        if (await state.GetAsync(collection, id, cancellationToken) is { } document)
            await state.DeleteAsync(collection, id, document.Version, cancellationToken);
    }
}
