---
title: Change your sign-in email
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# Change your sign-in email

::: info Availability
This flow is upcoming. The account API, SDK and dashboard must ship together; this guide does not announce production availability.
:::

1. Open **Profile → Change sign-in email** in the dashboard and enter your new address.
2. Choose **Verify identity and send code**. TerraScale asks for your password, or an explicitly requested email code for a passwordless account, plus a fresh two-factor code when enrolled. This confirmation expires within five minutes. Opening verification alone sends no email.
3. After verification, your new address receives a **six-digit code valid for ten minutes**. Your current address receives a notice with a cancel link valid for the same period.
4. Enter the code under **Enter the email change code** and choose **Change email and sign out**. Stay signed in to the same account while confirming.
5. **All sessions and sign-in tokens end, including this session.** Sign in again with your new address. The old address no longer signs in to this account. Linked social providers still identify you by their provider identity.

To cancel, open the link sent to your current address and confirm cancellation on that page. **Confirming cancellation signs you out everywhere** and voids the pending change; merely opening the link does not cancel it.

A new request replaces the previous change and code. Five incorrect codes void the change. An expired, canceled, replaced or already used change cannot be confirmed. Check the latest code or start again. If the address is already taken, choose another. If requests are rate limited, wait before starting again; requests are limited to three per account and three per target address per hour, with an additional shared sending budget.

Changing the address also prevents API-key and OAuth-client secrets bound to the previous account authentication state from exchanging for new tokens. Previously issued access tokens keep their normal bounded lifetime. After a connection failure during confirmation, try signing in with the new address before starting another change.

For integration security, see [confirm sensitive actions](/ja/guides/sensitive-actions). Keep verification proofs on the backend and send the browser only to the hosted verification URL. Never automatically replay a change request or confirmation after an uncertain response.

</div>
