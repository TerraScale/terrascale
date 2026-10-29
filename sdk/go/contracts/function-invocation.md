# Public function invocation

Producer pin: `terrascale@9081109f54e7ac2b20e20505e49eefa08b93840c`,
`src/TerraBase/Features/Functions/{InvokeFunctionEndpoint,FunctionRoute,FunctionService,FunctionRunner}.cs`.
This is source provenance, not a deployed-SHA claim.

`POST /v1/projects/{project}/databases/{database}/functions/{name}/invoke`
requires a project token with `documents:read`. Input is at most 1 MiB;
parsed JSON becomes `event.body`, other UTF-8 input becomes text, and empty
input becomes null. URL query values become `event.query`; repeated values
are comma-joined by the producer. Function names remain validated path segments.

The handler can return JSON with HTTP 200 or `{status, body}`. A string body
in the latter form produces text/plain; other bodies produce application/json.
The producer derives the media type; there is no handler contentType field.
`FunctionResponse` preserves the original successful HTTP status, media type
and bytes, including empty 204. Non-2xx handler responses and API refusals
remain `*APIError` with status, headers and raw body; these bytes need not be JSON.
Runtime errors answer 500 `function_error`. Declared producer refusals include
400, 404, 413 and 503. No pagination or idempotency contract exists for invocation.

An invocation may have executed before cancellation or transport failure.
Send it once, including when a handler returns 401, 429 or 503. The fixed
operation deadline includes token acquisition; caller cancellation remains earlier.
Never infer non-execution or replay a mutation from an HTTP handler outcome.

List, get, logs and deliveries require **both** `documents:read` and
`documents:write`, as do deploy/delete. Invocation alone needs `documents:read`.
Logs and deliveries are bounded retained arrays, not durable export/checkpoint APIs.
