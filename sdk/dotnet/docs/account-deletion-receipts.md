# Account deletion receipts

`TerraScaleAuthClient.Platform` preserves `DeleteAccountAsync(accessToken,
cancellationToken)` and adds:

```csharp
Task<bool> DeleteAccountAsync(string accessToken, string deletionReceipt,
    CancellationToken cancellationToken);
Task<bool> AccountDeletionCompletedAsync(string deletionReceipt,
    CancellationToken cancellationToken = default);
Task<bool> AccountDeletionOrganizationCompletedAsync(string deletionReceipt,
    string organizationId, CancellationToken cancellationToken = default);
```

The receipt DELETE overload requires its third argument to preserve existing
`DeleteAccountAsync(accessToken, default)` calls. Pass `CancellationToken.None`
when cancellation is unnecessary. Both status methods keep optional cancellation.

Generate a receipt from 32 cryptographically random bytes, encode it as canonical
unpadded base64url (43 characters), and retain it securely before sending the
authenticated DELETE to `/v1/terrascale/account`. The SDK validates the encoding,
including its final bits, before dispatch. Validation errors do not echo receipts.

The DELETE sends `X-TerraScale-Deletion-Receipt` alongside the sign-in bearer.
Only authenticated deletion creates this possession capability. Poll with
`AccountDeletionCompletedAsync`, which sends the same header to
`GET /v1/terrascale/account/deletion` without a bearer or configured API key.
Use a caller-owned HttpClient without default Authorization; the SDK rejects a
default Authorization header before sending a status request. Keep receipts out
of URLs, logs, browser storage, and client-visible responses. The dashboard keeps
its receipt server-side. Status exposes no user data and performs no deletion.

The account methods return `true` only for 204 and `false` only for 202. A 404 means the
receipt is unknown and throws `TerraScaleAuthException`; a 401 also throws.
Neither response proves completion. Other unexpected successes also throw.
Calls are cancellation-aware, bounded by the SDK request deadline, and sent once;
the caller controls subsequent polling. Paid cancellation behavior is unchanged.

For each original candidate organization, call
`AccountDeletionOrganizationCompletedAsync` with its ULID. This uses the same
receipt-only GET with `?organization=<ULID>`: 204 means the account deletion
completed and that organization's atomic deletion fence exists. A 202 returns
`false` and may mean incomplete deletion or a surviving shared organization;
it does not promise eventual removal. A noncandidate organization returns 404
and throws. Remove each local organization only after its explicit 204; account
completion alone does not prove every original organization was removed.

The release target is `0.0.8`: the dashboard already consumes qualified `0.0.7`
bytes, which must retain their package identity even though the latest stable tag
checked was `v0.0.6`. After the owning auth contract and SDK PR land with required checks,
tag the merged SDK main commit `v0.0.8`. The release workflow qualifies and tests
the package, publishes it to GitHub Packages, verifies a fresh installed consumer,
then publishes the same qualified artifact to nuget.org. Download the qualified
artifact with `gh run download <run-id> --repo TerraScale/ts-dotnet --name package
--dir <staging-dir>`. It contains `TerraScale.DotNet.0.0.8.nupkg` and
`provenance.json`. The dashboard maps this package exclusively to `vendor/dotnet`:
replace its package/provenance, update its central pin and lockfiles to `0.0.8`,
and run its `scripts/verify-sdk.py` as documented by that repository.
