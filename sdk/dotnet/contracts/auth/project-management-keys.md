# Project management credentials

Project methods send the supplied credential directly as a Bearer value.
An unrestricted project secret key authorizes GET with auth:read and mutations
with auth:write; the two scopes are independent. The key must match both the
project and its organization. Both IP allowlists apply. Revoked and expired keys
are read from storage per request and answer 401.

Step-up operations (OAuth client create/update/rotate and webhook create/rotate)
require a person's sign-in token and refuse keys with 403 person_required.
Authorized mutation requests commit an <operation_id>.requested audit event
before changing state, with the key ULID as actor.
