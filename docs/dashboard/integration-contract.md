# Dashboard integration contract

Open `/reference/integration` for Scalar or `/openapi/integration.json` for the
generated OpenAPI document. The committed document is
`contracts/dashboard/integration.openapi.json`.

The document covers annotated integration operations, starting with the current
session and workspace selection. It does not claim every dashboard page is a
public API. Existing `/api/v1` browser routes retain their paths and response
fields; newly introduced public integration operations use `/v1`.

Session operations use the server-managed `ts-dashboard-session` HttpOnly cookie.
Workspace changes require the same-origin `_csrf` form token and current access.
Documented cookie authentication does not bypass either check. SDK access tokens
and key secrets stay server-side.

Fast verifies the served document against the committed contract, authenticated
response fields, anonymous refusal and invalid CSRF refusal. A contract change
must update endpoint metadata and the committed document explicitly. The test
writes its generated document to a unique `test-results/openapi/*.json` file for
review. Scalar uses its packaged scripts and the request's CSP nonce.

Run `nice -n 19 make check` before advancing an artifact. Required CI includes
that check and the root Dockerfile's actual-application image build, with no image
push or deployment. Slow runs independently on main or manual dispatch, keeps a
red result on failure, and creates or updates a failure issue. Reports include
safe method identities, the commit, environment, reproduction and CI URL; raw
response bodies, argument values, credentials and detailed vulnerability output
are withheld. An infrastructure failure without a usable TRX is still reported.

The current package pin and HTTP fakes do not establish live producer parity.
SDK K and its package P are selected after the owning auth/TerraBase slices land;
the dashboard pin, provenance and lock files advance together once. Monorepo
producer tests and its SDK revision update remain with the freeze owners.
