# Hosted-UI test conversion playbook (temporary — delete when the conversion is done)

The ts-auth suite is being converted to drive the hosted UI through a real
headless Chromium (Playwright, C#) under TUnit. Everything stays in
`tests/TerraScale.Auth.Tests`, one test project, run the same way as before.

## What converts and what stays

CONVERT to the browser (Playwright):
- Any test that GETs a hosted HTML page (`/ui/{slug}/...`) and asserts on the
  rendered page, or that submits an HTML form (form-encoded POST) and follows
  the redirect the browser would follow.
- Any test whose subject is user-visible behavior: forms, validation errors,
  redirects, cookie posture, escaping (XSS), sign-in/sign-up/out flows.

STAY on `service.Http` (wire tests, unchanged shape):
- JSON endpoint calls (`PostAsJsonAsync` with `ok`/`error` bodies) — those are
  the pages' backing API, not pages.
- Management API (`/v1/...`), operator API, SCIM, OAuth token/discovery,
  webhooks, throttling/lockout/concurrency races (browser-driving races makes
  them flaky, not better).
- Store-level assertions (`service.Store...`) stay exactly as they are; only
  the DRIVER changes when a test converts.

When in doubt: if a browser user could observe the behavior, convert it; if
only an HTTP client could, leave it.

## Infrastructure (already in place — use it, don't rebuild it)

- `AuthServiceFixture` now boots the service on a REAL loopback origin:
  `service.Origin` (e.g. `http://localhost:54321/`). `TS_AUTH_PUBLIC_ORIGIN`
  IS that origin. `service.Http` still exists, no auto-redirect (302s visible).
- `BrowserFixture.NewPageAsync(options?)` → fresh isolated `IPage` per test.
  `await using var page = await BrowserFixture.NewPageAsync();`
  Dispose closes the context. `page.Context` for cookies.
- Absolute URLs: `new Uri(service.Origin, path).ToString()` — pages are
  navigated by absolute URL.

## Hard-won facts (do not rediscover these)

- The origin is `http://localhost:<port>` (NOT 127.0.0.1): Chromium only accepts
  the always-`Secure` session cookie over plain http for the localhost name.
- Per-class run filter syntax (the glob form matches NOTHING):
  `-- --treenode-filter "/*///<YourTestClass>/*"`
  Run the class binary directly for speed:
  `nice -n 19 dotnet tests/TerraScale.Auth.Tests/bin/Release/net11.0/TerraScale.Auth.Tests.dll --treenode-filter "/*///<Class>/*" --log-level None`

## The browser patterns

```csharp
var service = await AuthServiceFixture.GetAsync();
await using var page = await BrowserFixture.NewPageAsync();

// Navigate
await page.GotoAsync(new Uri(service.Origin, $"/ui/{slug}/sign-in").ToString());

// Fill + submit the real form (stable ids: #su-*, #si-*, #rp-*, #fp-*)
await page.FillAsync("#si-email", email);
await page.FillAsync("#si-password", "password123");
await page.ClickAsync("button:has-text('Sign in')");

// The browser follows the 302 itself: wait for the landing URL
await page.WaitForURLAsync("**/account");
await Assert.That(page.Url).Contains("/account");
```

- Bypass client-side validation when the ORIGINAL test posted a value the
  browser would block (e.g. short password): `await page.EvalOnSelectorAsync("form", "f => f.submit()");`
- Escaping/XSS: assert the hostile value round-trips as an attribute value or
  text (`InputValueAsync`, `TextContentAsync` — the DOM decodes entities), and
  fail on any script dialog:
  ```csharp
  page.Dialog += (_, dialog) => throw new InvalidOperationException($"script dialog fired: {dialog.Message}");
  ```
- Response status on navigation: `var response = await page.GotoAsync(url);`
  `response.Status` (int; 405, 200, ...).
- Cookies: `(await page.Context.CookiesAsync(service.Origin.ToString()))`
  → `.Secure`, `.HttpOnly`, `.SameSite` (Playwright `SameSiteAttribute`).
- Cross-site pages: route a fake origin, then navigate to it:
  ```csharp
  await page.RouteAsync("http://cross.example/**", r => r.FulfillAsync(new RouteFulfillOptions
      { ContentType = "text/html", Body = "<form id=f method=post action=TARGET></form><script>f.submit()</script>" }));
  await page.GotoAsync("http://cross.example/x");
  ```
- WebAuthn hosted flows: CDP virtual authenticator:
  ```csharp
  var cdp = await page.Context.NewCDPSessionAsync(page);
  await cdp.SendAsync("WebAuthn.enable");
  await cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object> {
      ["options"] = new Dictionary<string, object> {
          ["protocol"] = "ctap2", ["transport"] = "internal",
          ["hasResidentKey"] = true, ["hasUserVerification"] = true,
          ["isUserVerified"] = true, ["automaticPresenceSimulation"] = true,
      },
  });
  // page clicks then trigger navigator.credentials; fetch created ids:
  // await cdp.SendAsync("WebAuthn.getCredentials", ...) → result.credentials
  ```
  Keep origin-PINNING tests (foreign-origin rejection, challenge single-use
  through hand-built clientDataJSON) on the wire with `FakeAuthenticator` —
  the browser cannot forge a hostile origin.

## Origin fallout (must fix in every file you touch)

The public origin is now runtime (`service.Origin`), not `https://auth.test`:
- Every literal `https://auth.test/...` in an assertion/arrangement becomes
  fixture-derived: `$"{service.Origin}ui/{slug}/..."` (Origin keeps its
  trailing slash).
- WebAuthn: RP id is `service.Origin.Host` (`localhost`), clientData origin is
  `service.Origin.ToString().TrimEnd('/')`. `FakeAuthenticator` takes rpId and
  origin as REQUIRED constructor parameters now — construct with the runtime
  values.
- SAML ACS / OIDC callback URLs: derive from `service.Origin` the same way the
  service does.
- BillingE2eTests: the private app record exposes `app.Origin` — use it.

## Hard rules

- Never weaken an assertion; same expectations, real browser.
- Keep test names, `[NotInParallel]`, class structure, and the file's
  section comments — the diff should read as "driver changed", not "suite rewritten".
- Keep helpers that arrange via the store (e.g. `MintPasscodeAsync`) — they are
  the fixture-state API, not the driver.
- `TreatWarningsAsErrors` is on: no unused usings.
- Build+test your file BEFORE declaring done:
  ```bash
  flock /tmp/tsauth-e2e-build.lock -c 'nice -n 19 dotnet build tests/TerraScale.Auth.Tests/TerraScale.Auth.Tests.csproj -c Release --nologo'
  nice -n 19 dotnet run --project tests/TerraScale.Auth.Tests/TerraScale.Auth.Tests.csproj -c Release --no-build -- --treenode-filter "/*///<YourTestClass>/*" --log-level None
  ```
  (flock serializes concurrent builds; run tests only with --no-build.)
- If a build failure points at a file another agent owns, ignore that file —
  fix only yours; the orchestrator runs the full suite at the end.
