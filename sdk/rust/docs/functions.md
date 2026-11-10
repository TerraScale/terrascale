# Customer functions

The Rust SDK is not published. This directory is source only. Functions use the same project client and
customer authorization as SQL.

`Database::deploy_function` takes a `FunctionDeployment` containing JavaScript
which defines `handler(event)` and an optional five-field UTC cron.
`list_functions` omits source; `get_function` includes it.
`invoke_function` serializes the supplied JSON body and deserializes the handler's
result into your chosen type. Use `invoke_function_response` to retain a custom
successful status, headers and raw text or empty body. The SDK does not replay an
uncertain invocation.

Use `list_function_logs` for HTTP and cron invocation output. `delete_function`
stops the function and its schedule. Server-side validation, quotas and project
permissions apply to every method.
