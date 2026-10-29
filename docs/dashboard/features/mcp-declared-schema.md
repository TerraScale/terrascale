# Declared collection validation over MCP

`get_collection_schema` continues returning inferred fields and uniqueness declarations. Use `get_collection_validation` for declared `{schema, dialect}` validation. Both fields are explicitly null when detached (null in the SDK model); `schema: false` remains an attached rejecting schema.

`set_collection_schema` replaces declared validation and returns `{collection, status: "applied"}` only after producer success. It requires owner/admin management access. Platform databases remain read-only. The dashboard exchanges the current session for a project token with `documents:write` (read uses `documents:read`); personal access tokens are never forwarded to TerraBase.

The `schema` argument now explicitly accepts a JSON object or boolean, widening the old object-only MCP input. Existing object callers keep their request shape. `true` accepts all future local writes; `false` rejects them. Null, arrays, strings and numbers are rejected; null does not remove validation. Review generated MCP discovery/client schemas when integrating this change. The tool is marked destructive because replacing validation can weaken existing rules.

The official .NET SDK 0.0.12 supplies `PutDeclaredSchemaAsync` and `GetDeclaredSchemaAsync`, typed responses and generated JSON metadata. No SDK wire change or API version change is needed: both use `/v1/projects/{project}/databases/{database}/collections/{collection}/schema`. The producer accepts self-contained Draft 2020-12 schemas up to 64 KiB. Existing documents and replicated commits are not revalidated. Invalid schemas leave previous validation in place; upstream failures remain MCP errors.

Removal and preview remain separate dashboard workflows. Refresh client discovery when updating to this contract.

Document history, index recommendations, recommendation application and index migration progress are not exposed through MCP and are absent from discovery. Index recommendations and their application remain available through the data API and official SDK. Calls to their former names return an unknown-tool error. Rediscover tools after updating a client.
