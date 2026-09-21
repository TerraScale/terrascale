# Dashboard performance

The dashboard remains a native multi-page application: Razor Slices render full
HTML documents, forms POST to endpoints, and Datastar enhances local interaction.

## Delivery and rendering

`MapStaticAssets().ShortCircuit()` serves the .NET SDK's content-fingerprinted
asset routes, conditional responses and publish-time Brotli/gzip files. Public
assets bypass session activation and account persistence. `AssetUrls` resolves
those generated routes once at startup; development uses original paths for
`dotnet watch`. No hand-maintained hashes, additional CSS bundle, or request-time
file hashing is required.

`DocumentHtml` keeps login, standalone flows and the shell on the same resource
policy. The primary font is preloaded using the exact URL from the stylesheet so
it is downloaded once. Font URLs retain the framework's revalidation policy;
fingerprinted CSS, scripts and brand images use immutable caching in production.
The monospace font is loaded only when used. The stylesheet remains a single
source file; dynamic classes were not removed based on incomplete page coverage.

The shell counts databases once per render and uses direct icon calls instead of
reflection. Lower-page, read-only Usage charts opt into `content-visibility: auto`
with an intrinsic size; forms, menus and dialogs never use paint containment.
Dialog entry/exit uses `@starting-style` and discrete transitions. Cross-document
view transitions preserve shell continuity with 160 ms motion and are disabled
for reduced-motion users. No artificial loading delay or client router is added.

The framework retains main's Datastar v1 colon syntax and `$signal` references.
Browser regression checks cover mobile navigation and onboarding password
controls, and the mobile toggle exposes its expanded state to assistive tools.

## Speculation and freshness

Only the query-free demo routes `/`, `/databases`, and `/data` are eligible for
intent-triggered prefetch. Chromium's `moderate` eagerness uses hover/pointer
intent rather than fetching the entire site. Active navigation items are omitted.
Authentication, billing, secrets, admin pages, query variants, external links and
prerendering are excluded. Live mode rejects speculative requests *before* auth
validation and account persistence; its ordinary GET pipeline still writes state.

Rules are installed only in secure contexts with Speculation Rules support and
Chromium 138+ reported through UA Client Hints. This version floor is necessary
for `Clear-Site-Data: "prefetchCache", "prerenderCache"`. Demo mutation/login/logout
responses clear speculative HTML across tabs without evicting static assets or
cookies. Browser tests prove the old request is evicted; a fresh prefetch on the
next click is valid. Merely changing cookies is insufficient for same-origin
prefetch invalidation. If capability detection is unavailable or Save-Data is
requested, normal navigation remains fully functional.

Private document responses use `Cache-Control: private, no-store`. No unload
handler or custom page cache is installed. The navigation progress state clears
on page lifecycle events, Escape, errors, or a bounded cancellation timeout, and
never intercepts native navigation. Modified clicks, downloads, fragment links,
prevented events and canceled confirmations are ignored.

## Reproducing measurements

Measure **published Release builds**, not development servers. Use separate
origins/processes for the base commit and candidate, the same browser and machine,
and run them sequentially without other local builds or browser tests.

```sh
nice -n 10 dotnet publish src/TerraScale.Dashboard -c Release -o /tmp/dashboard-perf
# From the publish directory, use an unused port for each version:
TS_DASHBOARD_PRODUCTION_DEMO=1 DASHBOARD_E2E=1 \
  ASPNETCORE_URLS=http://0.0.0.0:5442 \
  Logging__LogLevel__Default=Warning dotnet TerraScale.Dashboard.dll

# From the repository, against an already-running published server:
DASHBOARD_E2E_BASE_URL=http://127.0.0.1:5442 \
  PERF_OUTPUT=test-results/optimized.json npm run perf
# To compare on the same machine:
DASHBOARD_E2E_BASE_URL=http://127.0.0.1:5442 \
  PERF_BASELINE=test-results/baseline.json npm run perf
```

The script uses five independent browser contexts per route/profile. Each makes
cold and warm document navigations, recording p75 LCP, CLS, TTFB, DOMContentLoaded,
transfer bytes, long tasks, individual resources and Server-Timing. Desktop uses
1440×900; mobile uses 390×844, 4× CPU slowdown and CDP network emulation (80 ms,
200 KB/s down, 100 KB/s up). These are laboratory measurements, not field INP or
user-population Core Web Vitals. Use the raw navigation timings to check that the
network shaping is effective in the runner environment.

The optional comparison rejects timing regressions greater than 20% plus 100 ms,
and CLS above the larger of 0.1 or baseline plus 0.02. Timing comparisons are not
an unconditional shared-CI gate: hardware contention makes that unreliable.
Browser resource/header, freshness, interaction and navigation assertions are
part of the regular Playwright suite. Reports and traces live in `test-results`.

Server-Timing separates authentication (`auth`), page model creation (`model`,
for `SlicePageEndpoint` pages), and time to response headers (`headers`). Razor
result execution for those page endpoints is measured by the
`TerraScale.Dashboard` meter's `dashboard.render.duration` histogram. Full render
duration is not mislabeled as a response-header metric and responses are not
buffered to compute it. Metric tags use endpoint names, not private request URLs.

## Platform references checked September 2026

- [Cross-document view transitions](https://developer.chrome.com/docs/web-platform/view-transitions/cross-document): same-origin MPA opt-in; normal navigation remains the fallback.
- [Speculation Rules and invalidation](https://developer.chrome.com/docs/web-platform/prerender-pages): clear speculative caches after state changes; do not prerender side-effectful flows.
- [Clear-Site-Data directives](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Clear-Site-Data): targeted prefetch/prerender cache invalidation.
- [Chrome 151](https://developer.chrome.com/blog/chrome-151-beta): form-submission prerendering and SPA soft-navigation metrics are outside this MPA pass.
- [Chrome 152](https://developer.chrome.com/blog/chrome-152-beta): viewport speculation heuristics remain an origin-trial experiment; no trial or browser flags are enabled.
- [Chrome 153](https://developer.chrome.com/blog/chrome-153-beta): newer overflow/scroll controls do not address the measured asset-delivery bottleneck and were not added speculatively.

Broad blur removal, `field-sizing` changes and more aggressive CSS pruning are
deferred: they change geometry or appearance without a measured benefit here.
