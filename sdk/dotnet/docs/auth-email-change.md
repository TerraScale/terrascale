# Changing a platform account's sign-in email

Dependency: the owning contract is [terrascale PR 822](https://github.com/TerraScale/terrascale/pull/822).
Merge and qualify the producer before publishing or activating this SDK operation.
HTTP-fake coverage does not prove live email delivery or hosted verification.

1. Call `auth.Platform.BeginStepUpAsync(bearer, new("change_email", "ts-auth", returnUri))`.
   `returnUri` is the registered dashboard origin's `/auth/step-up/callback`.
   Keep `Proof` on the backend and send the browser only to `StepUpUrl`.
2. After hosted fresh-credential verification (including active MFA), call
   `auth.Platform.RequestEmailChangeAsync(bearer, new(newEmail), proof)`.
   The response contains the normalized `NewEmail` and `ExpiresAt`. The new address
   receives a six-digit code valid for ten minutes. A new request replaces the old one.
3. From the same account authentication state, call
   `auth.Platform.ConfirmEmailChangeAsync(bearer, new(code))`.
   Success ends every session, refresh token and sign-in access token, including the
   caller's. Discard local sessions and sign in again with the new address.

The old address receives a cancel link. Confirming cancellation signs the account
out everywhere and voids the pending change. Five wrong codes also void it.
`email_change_denied` covers invalid, expired, replaced, canceled and replayed
changes. `email_taken` can occur at either step. `rate_limited` refuses a new request;
`step_up_required` requires starting hosted verification again. Mutations send once:
never automatically replay a proof or confirmation after a lost response.
