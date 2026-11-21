# Account-deletion project erasure

`TerraBaseManagementClient.EraseProjectAsync(projectId, authorityOrganizationId,
closureId, cancellationToken)` is ts-auth's explicit account-deletion consent
handoff. It sends `PUT /admin/projects/{project}/closure` with JSON
`{"version":2,"closureId":"<ULID>","authorityOrganizationId":"<ULID>"}` over the
existing private-listener transport behind the peer credential, without an
acting-user header. The caller must obtain account-deletion consent before
calling: version 2 authorizes a permanent project fence and TerraBase's purge of
the project's databases, TerraBase API keys and backup
destinations.

The result is `ProjectClosureResult`: the existing `TerraBaseProjectClosure`
when HTTP 200 answers `status: "accepted"` or HTTP 202 answers
`status: "pending"`, both naming the supplied `closureId`, and a refusal case
otherwise — `Conflict`, `Validation`, `Unauthorized`, `Forbidden`,
`QuotaExceeded`, `Unavailable` or `TerraBaseClosureUnsupported`. Pending is not
completed erasure. Other success statuses, contradictory states and another
closure ID answer `Unavailable` with code `invalid_response`. A 404, 405 or
501, and a 400 `unsupported`, `unsupported_version` or `invalid_request`,
answer `TerraBaseClosureUnsupported`: this TerraBase cannot serve the closure
contract, which neither proves closure completed nor refuses it. No refusal
case proves completion. Requests are sent once; a caller retry must retain the
same closure ID and authority organization.

`CloseProjectAsync` continues sending version 1 and remains empty-project-only.
The new method is additive; no package version or storage format changes here.
Both operations share transport and response validation, and use the existing
`closeProject` route-manifest entry because the HTTP method and path are the same.

Nothing in this SDK proves producer integration, database purge or live erasure. The parent task owns
the TerraBase implementation, integration evidence and producer pin after this
SDK change merges.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
