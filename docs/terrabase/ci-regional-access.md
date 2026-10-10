# Regional deployment access repair

[TS-773](https://ku.mariogk.com/terrascale/issue/TS-773) repairs automatic
deployment after Fast and publication succeed. Main run 985 published release 1725
but its deploy job 38240 failed to read both regional inventories. Explicit
GetObject calls authenticated with the registered `ts-service-release` API key
returned 404 for objects readable by the operator. GRU's report also lacked the
scope fields required by the discovery consumer.

On 2026-10-10, the existing `ts-service-release` policy was updated in place with
four additive statements. Its Vault-secret and legacy-prefix grants remain.
For each verified regional head bucket, CI may read the exact
`releases/orchestrator/status.json` object and read/create/overwrite
`releases/services/terrabase/*`. The added statements grant neither inventory writes nor object deletion.
Authenticated readback matched the six statements, and the actual CI identity
then read both inventories successfully.

| OCI object | Verified ID |
| --- | --- |
| CI user | `ocid1.user.oc1..aaaaaaaaqh53fajfk5gzpsbsshhkk7t3mmg2yxvy4gwky5b5awrdamjz3lqq` |
| Updated policy | `ocid1.policy.oc1..aaaaaaaa4qfdkliffggx5ivnv4qjuvl6aza2tth6ukvkotfepd6cgqqqftsa` |
| GRU control compartment | `ocid1.compartment.oc1..aaaaaaaaypdff62jbchkojfli5i22ybzm3qnptwwsbg3aa2t5ali6te52nzq` |
| SFO control compartment | `ocid1.compartment.oc1..aaaaaaaafwid62t6kwfqqw765ymv44y6z7bnpciudyhpob3eo5xkkacglqna` |
| GRU head bucket | `ocid1.bucket.oc1.sa-saopaulo-1.aaaaaaaacyfv72dwi7i4lzlqoqkrwgrchshwyd27q73ow545edbaodta5qda` |
| SFO head bucket | `ocid1.bucket.oc1.us-sanjose-1.aaaaaaaa7jedynxq5b77qer7cw5nyklcvo23xnxz2m5wq5lajomhhvdrfioq` |

The namespace is `grxnmoqvrcdv`; GRU uses `sa-saopaulo-1/gru-1-head` and SFO uses
`us-sanjose-1/sfo-1-head`. The controller dynamic groups already select their
exact control compartments, and their policies already cover current release
paths; neither group nor controller policy needed modification.

Inventory comes from the admitted controller's scope producer. Activating that
signed producer preserves controller quorum, retirement quarantine, replica
health gates and data. It does not fabricate inventory. Future main pushes use
the existing discovery command without a region list, scope override or deploy
switch. IAM readback and scope activation are prerequisites, not proof that a
release is running: acceptance still requires fresh complete reports with the
published source and binary digest in every discovered serving cell.
