# Live email evidence

`scripts/auth/email-flows-check.py` exercises actual hosted account creation,
code sign-in, existing-account signup, password reset and method lockouts using
free disposable inboxes. It attempts normal dashboard OIDC before protected
invitation/resend and alert triggers. Status subscription confirmation is a
separate product check; the synthetic subscription is removed without activating
incident updates. Fixed support/operations recipients and absent producers are
explicitly uncovered. Template sends do not substitute for product triggers.

Run only within an authorized live audit, with one live-send owner:

```sh
nice -n 19 python3 scripts/auth/email-flows-check.py
```

The protected alert path uses an empty owned personal workspace and one bounded
512 KiB record, because storage evaluation rounds GB to three decimals. It never
changes real alerts, billing, plans or customer resources. That path requires a
healthy signed-in session; an HTTP failure remains visible instead of bypassing
authentication. No CLI device-grant retry is part of the default run.

The default report is `/tmp/task-email-proof/all-flows.json`. It contains full
message hashes, recipient hashes, sender, timestamps and shape checks. Responses,
codes, subjects, passwords, cookies and signed links are never printed. Generated
test credentials alone are checkpointed atomically in a mode-600 private state
file under the mode-700 report directory. Never publish or commit that state.
Fresh runs refuse existing evidence or recovery state at the selected output
path; choose a different private output path for a separate audit. The initial
checkpoint is claimed atomically before any network work. Cleanup can recover
from the checkpoint alone if interruption prevented the first report write.

SIGINT/SIGTERM enters supported cleanup. If interrupted forcibly or management
authentication is unavailable, retain the checkpoint and report cleanup pending.
Confirmed account and inbox deletions are checkpointed independently and are
not repeated when another resource still needs cleanup. Pending dashboard
deletion uses its saved receipt cookie and the supported status form without
requiring a new sign-in; direct auth deletion uses its saved receipt header.
Each cleanup invocation performs one status continuation and keeps pending
state for a later invocation. Missing receipt records never count as deletion.
After sign-in is confirmed healthy, retry cleanup without another signup:

```sh
nice -n 19 python3 scripts/auth/email-flows-check.py --cleanup-state
```

`--resume-dashboard` continues only the existing account's protected product
paths before cleanup; it does not start another signup or reset. An incomplete
audit exits nonzero even when the individually delivered flows passed. The
existing `signup-mail-check.py` remains the narrow independent delivery probe.
