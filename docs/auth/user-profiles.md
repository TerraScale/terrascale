# Project user profiles and imports

Backend callers create users with `POST /v1/{project}/users`, update them with
`PATCH /v1/{project}/users/{user}`, and read profiles through the existing user
GET routes. Changes require a project secret API key with `auth:write`, or a
signed-in project administrator. Reads require `auth:read` or the viewer role.
An API-created user may have a password or no password; creation never sends email.

Profiles include `first_name`, `last_name` (255 characters each), `image_url`
(an absolute HTTPS URL, at most 2048 characters), and `external_id` (255 characters,
unique within the project). Control characters and bidirectional formatting characters
(U+061C, U+200E–U+200F, U+202A–U+202E, U+2066–U+2069) are refused. Set a scalar to null to clear it; omit it to retain it.

Each metadata value is a JSON object limited to 8192 UTF-8 bytes and a maximum
container depth of 10, counting the root object and nested objects and arrays. Updates use
JSON merge patch (RFC 7396): objects merge recursively, null removes a member,
and arrays and scalar members replace existing values. Both the submitted patch
and the resulting object must fit the size limit; the merged object must fit the depth limit on every write.

- `public_metadata` is returned to the user through userinfo and the ID token
  with the `profile` scope, alongside `given_name`, `family_name` and `picture`.
- `private_metadata` is returned only by backend management APIs. It is never
  copied to a token, userinfo, webhook, export, or the self-service response.
  Backend-visible means that GET callers with the viewer role or auth:read can read it;
  it is not restricted to project administrators.
- `unsafe_metadata` can also be changed by its user. Treat it as untrusted input,
  never as an authorization policy.

The hosted security page shows the user's name and avatar. A signed-in user
can `PATCH /v1/{project}/me/profile` with their live project session cookie and
an exact same-origin `Origin` header. Only first_name, last_name and unsafe_metadata
are accepted; attempts to write other fields are refused. Requests are limited to
20 per user and 60 per IP per project per minute across replicas, with 429 and
Retry-After on exhaustion. Unchanged patches write no user record and emit no event.

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

- bcrypt: canonical `$2a$`, `$2b$` or `$2y$`, cost 04–12, standard 60 characters.
- Argon2id PHC: version 19, 16-byte salt and 32-byte tag, canonical unpadded Base64;
  memory 8–65536 KiB, passes 1–10, lanes 1–8, memory at least eight times lanes,
  memory × passes at most 77824 KiB (twice the current 19456 KiB × 2 policy).
- Django PBKDF2-SHA256: `pbkdf2_sha256$iterations$salt$base64-hash`, 10000–1000000
  iterations, a 1–128-character plain-text salt encoded as UTF-8 (not Base64-decoded),
  and a 32-byte derived key encoded as padded Base64. The journey uses Django 4.2 and 5.2
  [known-answer vector](https://github.com/django/django/blob/stable/4.2.x/tests/auth_tests/test_hashers.py)
  for `lètmein` and salt `seasalt` at 600000 and 1000000 iterations, following the
  [documented password format](https://docs.djangoproject.com/en/4.2/topics/auth/passwords/).

After successful password verification, bcrypt, PBKDF2 and Argon2id hashes with
older parameters are replaced by a fresh hash using the current Argon2id policy.
A concurrent upgrade re-reads the account and verifies its current hash, continuing
sign-in only if its authentication stamp and epoch still match and it is enabled.
It never overwrites the winning hash or other concurrent state.

Each tenant can hold three of the four replica-local hashing slots; its FIFO
queue waits before taking a global slot. The combined admission deadline is five
seconds. Queue capacity is floor(3 × 5 seconds / measured duration of the current
password algorithm), with a minimum of one, instead of admitting work that cannot
meet the deadline.

At startup, the replica measures bcrypt cost 12, PBKDF2 at 1000000 iterations and
Argon2id at each accepted pass count with its largest accepted memory budget.
The fixed failure floor is the largest measured duration plus 50% headroom, at
least 300 ms. Every failed
password check, including unknown users, invalid credentials and imported hashes,
awaits the remaining floor after releasing its hashing slot. This deliberate timing
equalization consumes no hashing CPU or slot while waiting. Successful checks
are not padded. Unknown users verify the current Argon2id dummy hash.

email_verified preserves the import's supplied value; password possession alone
does not prove mailbox ownership. Social sign-in matches an existing account by email
only when the provider asserts email_verified. Administrators must not mark imported
unverified addresses as verified.

API creation/import emits user.created; profile changes emit user.updated in the
same persistence batch as the user mutation. Use the webhook event ID to deduplicate.

All five SDKs expose create, update and import alongside user reads. .NET uses
CreateUserAsync/UpdateUserAsync/ImportUsersAsync; JavaScript createUser/updateUser/importUsers;
Go CreateProjectUser/UpdateProjectUser/ImportProjectUsers; Python create_user/update_user/import_users
(including asyncio); Rust create_user/update_user/import_users. These source APIs ship
with the next SDK release; no new release is published by this change.
