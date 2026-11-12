# Project sign-up restrictions

Project administrators choose `methods.sign_up_mode`: `public`, `restricted` or
`waitlist`. The former `methods.sign_up` boolean is removed. The migration maps
true to public and false to restricted.

Public projects accept any address that passes restrictions. Restricted projects
accept an address on the allowlist or with a pending, unexpired invitation.
Waitlist projects apply the same admission rule; other addresses can request
access at `/ui/{project}/waitlist`, or by submitting the hosted sign-up form.
Joining the waitlist sends no email. Rejoining preserves the original entry.

`PUT /v1/{project}/settings` accepts a replacement `restrictions` object:

```json
{
  "methods": {"sign_up_mode": "restricted"},
  "restrictions": {
    "allowlist": ["person@example.com", "example.org"],
    "blocklist": ["blocked@example.org"],
    "block_disposable_emails": true,
    "block_email_aliases": true
  }
}
```

Lists accept at most 1,000 emails or exact domains each. Entries are trimmed,
lowercased and deduplicated. Domains do not match subdomains. Block rules take
precedence over invitations and allow rules. Alias blocking rejects `+` in the
local part. Disposable blocking uses the bundled gzip snapshot of
[disposable-email-domains](https://github.com/disposable-email-domains/disposable-email-domains/blob/main/disposable_email_blocklist.conf),
downloaded November 12, 2026, under its CC0 license. No runtime download occurs.

Admission is checked before sign-up email issuance and resends, and when creating
an account, including creation through a verified social provider. Refused
sign-ups use the same response for existing and unknown accounts. These rules
govern sign-up; existing users retain their normal sign-in methods.

## Invitations and waitlist

All management routes require the platform user's sign-in token and a project
role: viewer for lists, admin for changes. Lists accept `limit` (1–100, default
50) and `after`, and return `{items, next}`.

| Method and path under `/v1/{project}` | Result |
| --- | --- |
| `POST /invitations` | 201 invitation; body `{email, redirect_url?}` |
| `GET /invitations` | Invitation page |
| `DELETE /invitations/{invitation}` | 204; revoke pending invitation |
| `GET /waitlist` | Waitlist page |
| `POST /waitlist/{entry}/approve` | 204; atomically approve and create an emailed invitation |
| `POST /waitlist/{entry}/reject` | 204; reject without email |

Invitations contain `id`, `email`, nullable `redirect_url`, `created_at`,
`expires_at` and `status` (`pending`, `accepted`, `revoked` or computed `expired`).
Tokens are ULIDs, stored only as SHA-256 digests, expire seven days after creation
and never appear in management responses. Invitation emails use the existing
project-branded invitation template. Their link opens
`/ui/{project}/invitations/{token}` with the invited email prefilled. The user
chooses a password and verifies a code sent to that email. The invitation is
consumed atomically with account creation; opening the link alone does not use it.
A stale, accepted, revoked or expired link returns 404.

A redirect must exactly match a project `configuration.redirect_urls` entry. It
is checked again when hosted forms continue. Invitation email alone also permits
normal hosted sign-up, which still requires proving ownership of that mailbox.

Waitlist entries contain `id`, `project_id`, `email`, `created_at` and `status`
(`pending`, `approved` or `rejected`). Each email has one entry per project.
Concurrent decisions produce a 409 for the losing decision. A newly blocked
address cannot be approved until its restrictions change.

The dashboard exposes Users → Invitations, Users → Waitlist and Security →
Restrictions. The five SDKs expose invitation creation, listing and revocation,
and waitlist listing, approval and rejection. These changes are on the PR branch;
SDK packages have not been published.
