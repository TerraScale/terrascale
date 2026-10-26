# Browser SQL and libSQL HTTP access

The client listener's bearer-only CORS policy now includes these exact routes below
`/v1/projects/{project}/databases/{database}`:

- `POST /sql/execute`
- `POST /libsql/v2/pipeline`
- `POST /libsql/v3/pipeline`
- `POST /libsql/v3/cursor`

Browser preflights may request `Authorization` and `Content-Type` without sending a
bearer token. Actual requests still require a valid bearer credential and the
endpoint's existing scopes. Success and authentication-denial responses carry
`Access-Control-Allow-Origin: *`; cookie credentials are not allowed. The existing
allowed headers and exposed response headers also apply to these routes.

This does not grant CORS to legacy `/sql`, `/sql/explain`, unsupported libSQL
versions or operations (including v4 pipeline and v2 cursor), or extra path
segments. Management routes and internal listeners retain their existing policy.
There are no request/response schema, SDK contract, or storage format changes.

Fast integration coverage in `ClientCorsTests` uses real HTTP listeners for
preflight, unauthenticated denial, authenticated SQL responses, unsupported paths,
and management/internal exclusions. Build, Fast execution, CI and deployment
verification remain required before claiming this behavior is qualified or live.
