# Review packet: canonical reactive capability documentation

## Outcome

Public sync and runnable-snippet docs now name the canonical
`X-TerraScale-Capability` header. The retired `X-TerraScale-View-Capability`
name is absent from `docs/`.

The docs now state the source-backed lifetime: capabilities are issued on
600-second buckets and accepted for a maximum 1,200 seconds from issuance,
bound to the view, generation, Region, and authorization scope. They instruct
clients to attach a fresh view after expiry and never send API keys on reactive
cache requests.

## Revocation boundary

Existing legal/account-deletion promises that API keys are revoked and new
writes stop remain unchanged. The sync docs distinguish that ordinary API
guarantee from reactive capabilities: an already issued capability may remain
usable until expiry, at most 1,200 seconds, subject to service authorization
checks and revocation epochs. No stronger immediate-reactive-revocation
promise was added.

## Changed paths

- `docs/concepts/sync.md`
- `docs/snippets.md`
- 18 locale fallback copies regenerated with `scripts/i18n-sync.mjs`

No source repositories, legal drafts, `GOAL.md`, or `AGENTS.md` were changed.

## Verification

- `node scripts/i18n-sync.mjs --check` passed.
- `git diff --check` passed.
- `npm run build` passed; VitePress rendered the site in 16.21s.
- `rg -n -i 'x-terrascale-view-capability|X-TerraScale-View-Capability' docs`
  returned no matches.

This packet documents the public contract and terminology only; it does not
qualify deployed revocation propagation or launch readiness.
