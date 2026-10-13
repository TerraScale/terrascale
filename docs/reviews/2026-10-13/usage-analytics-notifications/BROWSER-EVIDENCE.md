# Browser evidence (local, uncommitted)

Audited production source: b719878083dffd9441e063bbfaccfa0dffa95abd. Captured on 2026-10-13 UTC against the synthetic PageAuditHost in this bound worktree, using the thread-owned incognito T3 collaborative browser tab. Local Tailscale origin during the session: http://delta.tail057f.ts.net:36335. This is a test-provider app, not live provider/payment verification.

The captures precede the final report/test commit and are **not current PR-head evidence**. No product source changed between them and the audit. Publication is held; attachment upload/linking and any required recapture await the coordinator's serial slot. AGENTS.md forbids committing screenshots/videos. Media is therefore local under obj/browser-evidence, with file hashes in the table below.

## Interactions and measured limits

1. **Desktop and narrow layouts:** configured 1440×900 and 390×844 CSS viewports. Notifications, Audit, Analytics and Usage had no document-level horizontal overflow. The mobile Audit table scrolls inside its focusable semantic table region (scrollWidth=696, clientWidth=348). Organization tabs also scroll within their own region; no new cosmetic finding is alleged. Stored image dimensions can differ from configured CSS dimensions, as listed below.
2. **Notifications:** /notifications?status=unread&page=2 displayed the final two of twelve synthetic visible records. /notifications/dialog opened by click/Enter. Initial focus was Close; Escape and Close restored focus to ts-notifications-topbar. Repeated reads/read-all and replica/scope behavior were checked through native HTTP journeys. The dialog has Close/Escape rather than a separate Cancel action.
3. **Analytics:** /analytics?range=1h rendered the existing valid synthetic provider sample. Empty, 503/error and recovered retry states were inspected; the error retained the selected database and GRU region. Home/End chart keyboard interaction was exercised with only one chart column, so this does not qualify multicolumn arrow traversal. Duplicate and sparse payload comparisons were HTTP/HTML evidence, not these screenshots.
4. **Usage:** /usage?range=30d&database=<synthetic-id> showed Last 30 days and audit-browser. Activating Spending controls then Overview reset to Last 7 days and All databases on desktop and narrow views. history.go(-2) restored the original 30d/database selection; history.go(2) returned to the reset page. The mobile video records the tab roundtrip without opening an account menu or showing an email.
5. **Limits:** browser Home captures, deliberately delayed/out-of-order responses, navigation interruption during slow reads, an automated accessibility audit and exhaustive repeated-browser-click stress were not run. No stale-response finding is claimed. Loading/status semantics were inspected in markup, not timed against a controlled slow provider. Desktop emails were replaced with “[email redacted]” before saved captures. All names/records are synthetic.

## Capture descriptions

| File (under obj/browser-evidence) | Route / state | Evidence |
|---|---|---|
| notifications-desktop.png / notifications-mobile.png | /notifications?status=unread&page=2 | Two page-2 records, unread filter and pager |
| audit-desktop.png / audit-mobile.png | /organization?tab=audit&range=24h&action=audit.unknown | Null-severity fixture counted as informational |
| analytics-desktop.png / analytics-mobile.png | /analytics?range=1h | One measured sample, 10 requests / 4.0 KiB |
| analytics-empty.png | /analytics?range=1h | No measurements message |
| analytics-error-mobile.png / analytics-retry-mobile.png | /analytics?range=1h&database=<synthetic-id>&region=GRU | Error and recovered measurements with filters retained |
| usage-desktop.png / usage-mobile.png | /usage?range=30d&database=<synthetic-id> | Selected database/30-day baseline, explicit unavailable Usage provider state |
| usage-filter-before-mobile.png / usage-filter-after-mobile.png | Selected route → Spending controls → Overview | Visible filter reset |
| usage-filter-loss-mobile.mp4 | Same mobile roundtrip | Approximately 21-second actual browser recording |

Pixels were inspected for the saved Analytics baseline/empty/error/retry, Usage baseline/before/after, Notifications desktop/mobile and Audit desktop/mobile. The video was captured by the browser recording tool; screenshots immediately before/after show its relevant states. An earlier desktop recording that may expose the synthetic account email is withheld and not included in the listed evidence.

Media hashes follow. They identify local files; they do not claim uploaded PR attachments.


| Local filename | Stored pixels / bytes | SHA-256 |
|---|---|---|
| analytics-desktop.png | 1280×800 | 9ff441074adf497396940b5861cc67a2fae69f4833b05a1eabbd24fd77b6188b |
| analytics-empty.png | 1280×800 | bf9467a536581b1cf9f792573921890fa8de5f7f6d069c92be1b48f0450b8016 |
| analytics-error-mobile.png | 780×1688 | 050fe4eca3df54791c382d89c30403cabd78a9554104df516a8e67ca5c83ba38 |
| analytics-mobile.png | 780×1688 | aeb8d4664130bb9f924ab488f0c23b07b286364ea9424fce121070ab525055fb |
| analytics-retry-mobile.png | 780×1688 | 5cf4ec3adefe368e84f218ca37e0b717f18927ccd2cb84ae565d2178f6e65a53 |
| audit-desktop.png | 1280×800 | 7437593019d75e0a0e292e95d96a8fc10e55d028c0e53431ebd95796a153de43 |
| audit-mobile.png | 780×1688 | 84ea833ad80729dcadb0df41bb1b08da6d7fa5871c08e25ea43c0ae31990f79e |
| notifications-desktop.png | 1280×800 | 020225b2d7257a54775439130173d18f627a307ed4ba3e5cc7667e378e6cdc44 |
| notifications-mobile.png | 780×1688 | 1d59a251751e94eb0e897ea7c228d6d22cc1e66ba68be50758653a1a4291fccb |
| usage-desktop.png | 1280×800 | 2ebf8da2981c5b2f3f2c1304fd3bb599803f5e74d7bac3782dd62b886f65c982 |
| usage-filter-after-mobile.png | 780×1688 | 5f3b32864b66a35f546331076499da709419042be4d2f63b549d50899607b610 |
| usage-filter-before-mobile.png | 780×1688 | d0ec4854bdecb5b521fc2b16919c1a87098bbec896292bf602a66ea176685f3a |
| usage-filter-loss-mobile.mp4 | 118980 bytes | c74de3a183bc5d692f9c6d7af43c90beff5940208fca23b8facfbf63c9649273 |
| usage-mobile.png | 780×1688 | 515679c895fabfd3a611af82165ee3846579e0eebd338066f121684f16166d32 |
