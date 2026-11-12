# Project user profiles and imports

Backend callers create users with `POST /v1/{project}/users`, update them with
`PATCH /v1/{project}/users/{user}`, and read profiles through the existing user
GET routes. Changes require a project secret API key with `auth:write`, or a
signed-in project administrator. Reads require `auth:read` or the viewer role.
An API-created user may have a password or no password; creation never sends email.

Profiles include `first_name`, `last_name` (255 characters each), `image_url`
(an absolute HTTPS URL, at most 2048 characters), and `external_id` (255 characters,
unique within the project). Set a scalar to null to clear it; omit it to retain it.

Each metadata value is a JSON object limited to 8192 UTF-8 bytes. Updates use
JSON merge patch (RFC 7396): objects merge recursively, null removes a member,
and arrays and scalar members replace existing values. Both the submitted patch
and the resulting object must fit the limit.

- `public_metadata` is returned to the user through userinfo and the ID token
  with the `profile` scope, alongside `given_name`, `family_name` and `picture`.
- `private_metadata` is returned only by backend management APIs. It is never
  copied to a token, userinfo, or the self-service response.
- `unsafe_metadata` can also be changed by its user. Treat it as untrusted input,
  never as an authorization policy.

The hosted security page shows the user's name and avatar. A signed-in user
can `PATCH /v1/{project}/me/profile` with their live project session cookie and
an exact same-origin `Origin` header. Only first_name, last_name and unsafe_metadata
are accepted; attempts to write other fields are refused.

The dashboard's Authentication → Users section links each user to a detail page.
Administrators edit profile fields and metadata there; other project members
have read-only fields. The metadata editors submit merge patches.

## Import

`POST /v1/{project}/users/import` accepts `{"users":[...]}` with 1–500 rows and
returns `{"items":[{"index":0,"user":{...}}],"errors":[{"index":1,"code":"invalid_request","message":"..."}]}`.
Indexes are zero-based input positions. Valid rows commit independently, without
email, even when another row has an invalid hash or a duplicate email/external ID.
The limit is ten calls per project per minute, shared by service replicas.
A lost response can leave committed rows; reconcile using email before resubmitting.

Rows accept email, all profile and metadata fields, email_verified (default false),
and an optional password_hash. Hashes are never returned. Accepted encodings:

- bcrypt: canonical `$2a$`, `$2b$` or `$2y$`, cost 04–14, standard 60 characters.
- Argon2id PHC: version 19, 16-byte salt and 32-byte tag, canonical unpadded Base64;
  memory 8–65536 KiB, passes 1–10, lanes 1–8, memory at least eight times lanes,
  memory × passes at most 196608 KiB.
- PBKDF2-SHA256: `pbkdf2_sha256$iterations$base64-salt$base64-hash`, 10000–1000000
  iterations, a 16–64-byte salt, and a 32-byte derived key. Base64 includes padding.

After successful password verification, bcrypt, PBKDF2 and Argon2id hashes with
older parameters are replaced by a fresh hash using the current Argon2id policy.
A concurrent account change refuses that sign-in rather than overwriting new state.
email_verified preserves the import's supplied value; password possession alone
does not prove mailbox ownership.

API creation/import emits user.created; profile changes emit user.updated in the
same persistence batch as the user mutation. Use the webhook event ID to deduplicate.

All five SDKs expose create, update and import alongside user reads. .NET uses
CreateUserAsync/UpdateUserAsync/ImportUsersAsync; JavaScript createUser/updateUser/importUsers;
Go CreateProjectUser/UpdateProjectUser/ImportProjectUsers; Python create_user/update_user/import_users
(including asyncio); Rust create_user/update_user/import_users. These source APIs ship
with the next SDK release; no new release is published by this change.
