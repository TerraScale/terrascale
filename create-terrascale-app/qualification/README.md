# Connected browser qualification

`browser-journey.mjs` drives an already running generated starter against its
configured public authorization and database services. It never starts an
engine, intercepts a request, installs browser credentials directly, or replaces
a service. A missing fixture fails the command; it is not a skipped success.

Install this package's development dependencies and Chromium:

```sh
npm install
npx playwright install chromium
TERRASCALE_STARTER_JOURNEY_CONFIG=/private/starter-fixture.json npm run qualify:browser
```

Generate the app with the package CLI, install the identified SDK artifact, set
its `.env.local` to the qualification database/project/API/auth/callback values,
and run `npm run build && npm start`. The exact callback must be registered in
the authorization service. Apply the starter schema to the cloud branch before
running the browser harness. Identify the generated app, SDK, and service
artifacts in the fixture record. Use the same account in both entries when
qualifying that independent sessions share the same authorized view.

The private JSON fixture requires:

- `appUrl`, `authUrl`: the running generated app and authorization service URLs.
- `accounts`: two objects with `email` and `password` for existing eligible
  accounts that can access the configured project/database. The harness signs
  in through the actual public email sign-in endpoint and approves the actual
  consent UI. Account creation and cloud provisioning are prerequisite
  qualification steps, not replaced by this harness.
- `consentMode`: omit to exercise the dashboard consent UI. Use `public-api`
  only for an installed authority fixture without dashboard UI; it approves
  through the real OAuth consent endpoint and validates the callback. The
  result records this narrower mode; it does not qualify dashboard UI.
- `revocationBoundMs`: the deployed service's declared maximum propagation bound,
  greater than zero and at most 300000 milliseconds.
- `sources`: source revisions for `starter`, `sdk`, `database`, and `auth`.
- `artifacts`: SHA-256 digests for the same four components from their build owners.
- `resultPath`: optional destination for a credential-free JSON result.

Keep the fixture outside the checkout. Do not include passwords, session
cookies, token responses, network traces, or the fixture itself in evidence.
The result records supplied source/artifact identifiers, completed scenarios,
and whether all five scenarios passed. A failure leaves `complete: false`.
The harness writes uniquely named posts to the qualification branch; retain
that branch with the evidence or delete it through the public database API
when no longer needed.

Pass conditions are two real independent sign-ins sharing the same view,
two-way writes visible in both browsers, actual rotating OAuth refresh after
65 seconds, network disconnect and catch-up, session revocation stopping writes
and delivery within the declared bound while preserving the peer, and a fresh
sign-in that catches up and resumes writes. This does not measure the
fresh-machine five-minute target or prove a real CDN/Worker traffic topology.
Those require their own deployed-environment evidence.
