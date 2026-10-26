# Project closure and network policy

These operations follow producer revision `a3cbf8b1c73d304189a489fa8033d41f3b508de3`:
`src/TerraScale.Authentication/Features/Platform/PlatformEndpoints.cs` defines
`delete_project` and `set_project_allowed_cidrs`; the same directory's
`ApiKeyEndpoints.cs` defines nullable `last_used_at`. Both operations require
a platform-user sign-in bearer token and project-admin authority.

`DELETE /v1/terrascale/organizations/{organization}/projects/{project}` has no
request body. Its success outcomes are 202 and 204. The CIDR operation is
`PUT` on that path plus `/allowed-cidrs`, accepts JSON, and returns 200 with
the updated project. Authentication, role, missing-resource and availability
errors remain typed API errors. Neither operation is automatically retried.

`User.DeleteProject` returns `(ProjectDeletionResult, error)`. Handle pending
closure explicitly; it makes one request and does not poll or retry:

```go
result, err := user.DeleteProject(ctx, organizationID, projectID)
if err != nil {
    return err // *tsauth.APIError retains 409 not_empty/closure_conflict/binding_conflict
}
switch result {
case tsauth.ProjectDeletionPending:
    // HTTP 202: closure is pending. A later explicit DELETE can resume it.
case tsauth.ProjectDeletionCompleted:
    // HTTP 204: logical closure completed; this does not certify physical erasure.
}
```

This replaces the former error-only signature. `v1.ManagementUser.DeleteProject`
is a separate TerraBase operation whose contract declares only 204 completion.

`user.SetProjectAllowedCIDRs(ctx, organizationID, projectID, cidrs)` requires
project admin. Pass an explicit `[]string{}` to clear the policy; a nil slice
sends `allowed_cidrs:null`, which the server rejects. The server accepts up to
32 IPv4/IPv6 CIDRs or addresses. The returned `Project.AllowedCIDRs` contains the
canonical policy, also available through create/get/list/rename responses.
Newly issued key tokens adopt changes within 60 seconds and remain subject to
the key's own policy.

`APIKey.LastUsedAt` is a nullable `*time.Time`: nil means never exchanged;
a successful exchange timestamp may be up to five minutes stale.
