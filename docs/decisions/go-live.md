# Go-live deferrals

DECISION: Defer SDK publishing, legal terms and other launch formalities until the user says we are going live. Reason: user order 2026-10-30: "add to the docs/decisions to move all sdk publishing when i say that we are going live and move all that annoying stuff to when going live like terms and etc".

Nothing below starts until the user says we are going live.

## Checklist

- [ ] SDK publishing to npm, NuGet, PyPI and crates.io, including registry setup and first releases. See `.cos/monorepo-20261029/registry-handoff.md`. Workflows `.github/workflows/sdk-*-release.yml` stay tag-only; push no `sdk/<lang>/v*` tags before go-live. Go module tags may continue.
- [ ] Legal: terms of service, privacy policy, DPA, cookie notice, acceptable use, and signup consent text. Existing pages are in `docs-site/docs/legal/`. No cookie notice or acceptable-use page exists yet.
- [ ] Roll all secrets. Closed-beta rule: the user rolls everything at go-public.
- [ ] Other launch formalities found on main, listed and not started:
  - `docs-site/docs/legal/security.md` still carries the "Draft — not yet in effect" banner.
  - `docs-site/docs/legal/terms.md`, `privacy.md` and `dpa.md` already carry effective dates (2026-10-23/24); confirm or reset them at go-live.
  - Locale copies of the legal pages exist under `docs-site/docs/<locale>/legal/` (ar, de, es, fr, hi and others); they need the same go-live review.
  - `.github/workflows/sdk-dotnet-release.yml` also has `workflow_dispatch`, so it is not tag-only as the SDK rule requires; decide at go-live.
