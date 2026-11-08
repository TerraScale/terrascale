# Onboarding: Free card first, selected by default (TS-1426)

Before/after screenshots of the onboarding "Choose your plan" step, captured
at 1280×800 and 375×700 on the synthetic fixture host
(`dotnet run --project tests/TerraScale.Dashboard.E2E -- --dashboard-fixture-host`).
`before-*` is `origin/main` (485525c45); `after-*` is this change.

## What changed

| Evidence | Before | After |
| --- | --- | --- |
| Plan cards | 3 paid cards (Starter, Pro, Team); Starter preselected | Free first and selected by default, then Starter, Pro, Team |
| Primary action | "Continue to checkout" always | Follows the selection: "Continue on Free" / "Continue to checkout" |
| Secondary action | "Continue on Free" (bypass button) | Removed |
| Free card copy | — | "$0.00 / month · No card required" |

## Files

- `before-1280.png`, `before-375.png` — 3 paid cards, Starter checked, bypass
  button next to the primary checkout button.
- `after-1280.png`, `after-375.png` — Free card first and checked; the single
  primary action reads "Continue on Free".
- `after-starter-1280.png`, `after-starter-375.png` — with Starter selected the
  same primary slot reads "Continue to checkout"; no secondary action appears.
