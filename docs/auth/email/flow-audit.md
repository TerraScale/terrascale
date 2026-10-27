# Email flow audit, 2026-10-27

The original missing signup message was not reproduced. The live GRU sender had
OCI Vault credentials and available Cloudflare capacity. A fresh hosted signup
delivered a full verification message fetched through mail.tm at 14:17:29 UTC;
its inbox was deleted without redeeming the code. No production configuration
change was needed. This does not establish the cause of the earlier incident.

The audit covers auth email producers and dashboard callers, plus the separate
orchestrator and status delivery surfaces. All auth producers share the provider
validation described in [provider acceptance](provider-acceptance.md). Hosted
locale and tenant corrections are described in
[hosted locales and tenants](hosted-locales-and-tenants.md), and queue overload
handling in [queue admission](queue-admission.md).

| Actual flow or surface | Coverage and live proof constraints |
| --- | --- |
| Platform signup verification | Fresh hosted signup and full disposable-inbox API fetch proven before merge; post-deployment proof remains required. |
| Existing-account signup fallback | Sends a sign-in code, discarding the submitted new password. Live full-message delivery proven. |
| Platform email-code sign-in | Live full-message delivery proven. Hosted session redemption and protected OAuth access are separate observations. |
| Platform password reset | Live full-message delivery proven; the replacement password authenticated through hosted auth. |
| Password, sign-in/signup-code and reset-code lockouts | Live full-message delivery proven for password, sign-in-code and reset-code notices. Signup-code shares the sign-in-code budget. Fast coverage includes project isolation and negotiated locale. |
| Project end-user signup, sign-in and reset | Same hosted routes in the project tenant. Project creation and ownership access are prerequisites; signed-in live proofs remain pending while access is unavailable. |
| Organization/project invitations and resend | Actual invite creation reserves capacity before persistence. Dashboard resend revokes and creates a replacement. Signed-in live proofs remain pending. |
| Operations allowance at 80% and 100% | Dashboard measured-usage producer and durable auth relay. Forcing real quota consumption is outside this isolated delivery check; controlled HTTP integration tests cover delivery outcomes. |
| Storage allowance | Auth renderer/relay exists; current dashboard has no storage allowance producer. Do not count a renderer test as producer proof. |
| Dashboard alert firing and recovery | Actual measured-project alert producer and durable auth relay. Live harness uses only an owned empty project and tiny synthetic storage after supported signed-in access works. |
| Support request and acknowledgement | Fixed staff support recipient precedes the requester acknowledgement. A disposable acknowledgement cannot prove staff inbox delivery; an isolated support sandbox is needed for full proof. |
| Infrastructure alerts and digest | Separate ts-orchestrator relay with fixed operational recipients. No recipient overrides or production incident injection are part of this audit. |
| Status subscription confirmation/events | Separate status service. Disposable subscription confirmation is safe with cleanup; publishing incidents would notify real subscribers and is excluded. |
| Dashboard in-app notifications | No general email producer. In-app notification delivery is a separate surface. |
| Template-test API | Explicit provider acceptance and rejection coverage. Template delivery alone does not prove a product producer. |

No email-change, magic-link, MFA-email, new-device or ownership-transfer email
producer exists in the audited source. Some templates exist without producers;
their absence is recorded rather than counted as delivered or implemented here.

`scripts/auth/email-flows-check.py` records full-message proof separately from
failed prerequisites and uncovered flows. Generated credentials/checkpoints and
all evidence stay private; sanitized reports contain only hashes, timestamps,
fixed reasons and request IDs. Each send is a real supported product trigger.
Protected proofs wait for supported sign-in to work; no authentication bypass
or copied production service credential substitutes for it.

The first expanded live run was interrupted by an unrelated heavy job. Its
memory-only account/inbox credentials were lost, so cleanup of that run remains
unconfirmed. The replacement harness checkpoints generated credentials with
mode 600 and attempts cleanup on SIGTERM/SIGINT. An unconfirmed cleanup must
remain visible independently of successful delivery evidence.

The replacement run fetched verification at 14:44:31 UTC, sign-in at 14:45:52,
existing-account signup fallback at 14:47:18, code-lockout notice at 14:48:38,
reset at 14:48:54, password-lockout notice at 14:56:49 and reset-lockout notice
at 14:57:09. Its disposable inboxes were deleted. Synthetic account cleanup is
pending supported signed-in management access; the private checkpoint is retained.
The optional status subscription check received HTTP 403 before any subscription
POST, so no subscription was created and no delivery is claimed. Protected
proofs remain pending until the user confirms the separate sign-in fix is live.

The independent daily signup workflow runs reviewed main code, never redeems a
signup code, and reports failures without making production delivery an
unreliable external dependency of required CI. Required Fast integration tests
remain the merge gate. Provider acceptance, inbox delivery and final resource
cleanup are distinct results; none implies the others.
