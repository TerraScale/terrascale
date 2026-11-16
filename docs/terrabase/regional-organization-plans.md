# Organization plans in every region

The dashboard owns billing plans and maps pricing.json v2 allowances to TerraBase plans. A plan publication through the organization, enrolled-project or management plan endpoint publishes the same plan to every discovered region. Peers use the internal listener and peer credential. A receiving peer stores the publication locally without forwarding it again.

Publication succeeds only after every region accepts it. A failed or rejected peer publication returns `503 plan_unpublished`; retry the same request. The dashboard's existing plan reconciler retries failed publications and periodically republishes settled plans. Updates and downgrades invalidate the local admission cache through the catalog commit sequence.

The dashboard requires a successful publication before completing personal or business organization creation. A failed publication leaves setup incomplete; retrying adopts the same organization and publishes again before returning success. This includes Free organizations, whose explicit Hobby publication must reach every region before their first database request.

For ts-auth storage organizations, publication records the plan explicitly, including Hobby. Publishing Hobby before the first database now creates the memberless storage organization and returns `200`, just as paid plans do. Enrollment adopts that record. Deleted organizations and owner-managed organizations still return `404` on the storage plan endpoint.

A clustered region with no organization plan, or only an enrollment-created Hobby default without a publication, returns `503 plan_unknown` at admission. It does not admit that organization under Hobby while billing publication is pending. Existing paid records retain their plan; owner-created organizations retain their explicitly created plan. A standalone region retains local admission defaults. Only the region's writer makes this check: a node that is not its region's writer executes no client request (it forwards each one to the writer, which admits it), so it answers with the organization's recorded plan and never `plan_unknown`.

The publication marker uses the existing system catalog record kind and key `organizations/{id}/state/plan`, with the plan name as its value. No router or replication contracts change. Read limits, write admission, storage and operation allowances consume the published local plan.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
