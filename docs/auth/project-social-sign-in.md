# Project social sign-in

A project's end users can sign in to the project's hosted pages with Google, GitHub, Apple or any OpenID Connect
provider. The customer brings its own OAuth client for each provider; ts-auth stores the client per project and never
shares one across projects. Builds on [project tenant management](project-tenant-management.md).

## Configuration API

Every call takes the caller's terrascale sign-in token, like the other `/v1/{project}/…` calls.

| Call | Role | Effect |
|---|---|---|
| `GET /v1/{project}/settings` | viewer | `providers` lists google, github, apple and oidc, in that order, each with `configured`, its public fields and the `callback_url` to register at the provider. |
| `PUT /v1/{project}/providers/{provider}` | admin | Configures or changes the provider; answers the settings. |
| `DELETE /v1/{project}/providers/{provider}` | admin | Removes the provider and its secret; answers the settings. |

`PUT` body (snake_case JSON):

| Provider | Fields |
|---|---|
| `google`, `github` | `client_id`, `client_secret` |
| `apple` | `client_id` (the Services ID), `client_secret` (the `.p8` private key, an EC P-256 PEM), `team_id`, `key_id` |
| `oidc` | `issuer` (https, no query or fragment), `client_id`, `client_secret` |

`client_secret` is required the first time; omitted, the stored secret is kept. An unknown provider is 404 and an
invalid field 400 `invalid_request`.

## Secrets

The client secret (or Apple's private key) is sealed with `SecretBox` (AES-256-GCM under the OCI Vault master key)
with the project and provider as associated data, so a sealed secret copied to another project or provider does not
open. It lives in the project's `project_auth_settings` document and is never answered, logged or rendered. Audit logs
name the user, project and provider only.
