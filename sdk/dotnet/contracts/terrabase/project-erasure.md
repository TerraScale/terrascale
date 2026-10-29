# Account-deletion project erasure

`TerraBaseManagementClient.EraseProjectAsync(projectId, authorityOrganizationId,
closureId, cancellationToken)` is ts-auth's explicit account-deletion consent
handoff. It sends `PUT /admin/projects/{project}/closure` with JSON
`{"version":2,"closureId":"<ULID>","authorityOrganizationId":"<ULID>"}` over the
existing private-listener transport behind the peer credential, without an
acting-user header. The caller must obtain account-deletion consent before
calling: version 2 authorizes a permanent project fence and TerraBase's purge of
the project's databases regardless of replication, TerraBase API keys and backup
destinations.

The result is the existing `TerraBaseProjectClosure`: only HTTP 200 with
`status: "accepted"` or HTTP 202 with `status: "pending"`, both naming the supplied
`closureId`, are valid. Pending is not completed erasure. Other success statuses,
contradictory states and another closure ID throw `invalid_response`. Failures,
including 404, do not prove completion. Requests are sent once; a caller retry
must retain the same closure ID and authority organization.

`CloseProjectAsync` continues sending version 1 and remains empty-project-only.
The new method is additive; no package version or storage format changes here.
Both operations share transport and response validation, and use the existing
`closeProject` route-manifest entry because the HTTP method and path are the same.

Coverage uses the established SDK HTTP handlers to verify both versions' wire
requests, accepted/pending responses, response mismatches and conflict handling.
The version 2 server patch is not available to this SDK suite; these tests do not
prove producer integration, database purge or live erasure. The parent task owns
the TerraBase implementation, integration evidence and producer pin after this
SDK change merges.
