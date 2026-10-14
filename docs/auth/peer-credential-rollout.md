# TerraBase peer credential rollout

Both ts-auth storage transports and TerraBase's replication and catalog clients
send `TERRABASE_PEER_SECRET` as `X-TerraBase-Peer-Credential`. The secret comes
from Fly secrets: 32 random bytes encoded as 64 hexadecimal characters. Missing
or malformed secrets refuse either service's startup on Fly. Errors name the
variable without echoing its value. Public clients keep their own credentials.
Peer credentials and Fly identity come only from the process environment;
application settings files cannot supply them. Integration tests inject their
own synthetic configuration before startup.

PR1 verifies and records credential outcomes on every internal request without
refusing unmatched requests. PR2 adds refusal only after T2 deployment works and
PR1 is live in gru, sjc and fra. There is no rollout flag or switch.

Off Fly, a missing secret sends no peer header; such requests are recorded as
unmatched in PR1 and will be refused after PR2. Production apps share one secret;
each preview generates its own once and maps it into both containers alongside
their other required secrets. Generate into a mode-0600 temporary file outside
Git, pipe it to `fly secrets set --stage --app APP TERRABASE_PEER_SECRET=-`, and
delete it. Never put its value in arguments, logs, chat or source. Staging does
not deploy machines.

See [the TerraBase rollout contract](../terrabase/peer-authentication.md) for
metrics, enforcement and rotation behavior.

Before PR2, `terrabase_peer_authentication_requests_total{credential="unmatched"}`
must stay flat on every TerraBase machine in gru, sjc and fra for 24 hours after
PR1 is live on all machines.
