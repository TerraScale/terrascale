# A new code replaces the previous one

QA round 4, auth:5 (P0, security). Since #763, a resend kept the code it replaced redeemable until that code's own
expiry, so a superseded code still signed in. This reverses that part of `code-lifetime.md`:

- A passcode record holds exactly one code. A new sign-in, sign-up verification or password-reset code replaces the
  previous one, which never redeems again. Step-up codes are one per step-up request already.
- Codes still last 30 minutes (`Passcodes.Lifetime`) and the 5-attempt budget per email per 30 minutes is unchanged.
- A superseded code fails like a wrong one; the hosted error says "Check the latest email, or request a new code."
- Storage: passcode records no longer carry `previousCodeHash` or `previousExpiresAt`. Records written before this
  change still hold them, but nothing reads them, so their earlier code is dead too.
