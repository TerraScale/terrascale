# Organization policy and account MFA status

Source contract: [mfa-policy-provenance.json](mfa-policy-provenance.json).
Use the caller's platform **sign-in** access token with the source SDK:

```csharp
var mfa = await auth.Platform.GetAccountMfaAsync(accessToken, cancellationToken);
// Present mfa.ManageUrl for explicit navigation to hosted factor management.
var organization = await auth.Platform.SetOrganizationMfaPolicyAsync(
    accessToken, organizationId, new(RequireMfa: true), cancellationToken);
```

The setter requires organization owner; enabling also requires that owner's
active factor (`409 mfa_not_enrolled`). Members without a factor encounter
`403 mfa_required`. Pass explicit `true` or `false` (no default); `false` removes the policy. This is
an authorization policy, not an SDK feature switch. Both calls are sent once
and honor cancellation. Enrollment remains on the hosted page. Every organization
DTO now requires `RequireMfa`, including constructor calls; it has no default.
Account status requires `Enabled`, `RecoveryCodesRemaining` (0–8), and `ManageUrl`.
Static contract coverage does not establish live MFA qualification.
