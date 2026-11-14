# Project user profiles

Producer: [C48 contract](../../../docs/auth/user-profiles.md).

| Method and path | Operation | Scope | Response |
| --- | --- | --- | --- |
| POST /v1/{project}/users | create_project_user | auth:write | 201 ProjectUserBody |
| PATCH /v1/{project}/users/{user} | update_project_user | auth:write | 200 ProjectUserBody |
| POST /v1/{project}/users/import | import_project_users | auth:write | 200 ImportProjectUsersBody |

User GET responses include nullable first_name, last_name, image_url and external_id,
email_verified, and public_metadata/private_metadata/unsafe_metadata objects.
Only backend management responses include private_metadata.
