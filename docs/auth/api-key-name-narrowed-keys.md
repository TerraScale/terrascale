# API keys narrowed by a database name

Since #778, project API keys and the GitHub Actions e2e binding narrow to a database by its ULID only, and TerraBase
refuses a token whose `database` claim is a name. Keys stored before that change still hold a database name, which can
move to another database after a rename, or after a deletion and re-creation.

ts-auth refuses to mint a token for such a key: the client-credentials grant answers 401 `invalid_client` with "This
API key is narrowed to a database by name. Create a new key narrowed by the database ID and revoke this one." The
refusal names the fix, where the token TerraBase would later refuse only says it is invalid. There is no migration:
recreate these keys narrowed by the database ULID.

QA round 4, cli:100.
