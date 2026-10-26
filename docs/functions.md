# Customer functions

Install the Rust SDK from an immutable Git revision; crates.io publication is
pending the user-owned release step. Functions use the same project client and
customer authorization as documents.

`Database::deploy_function` takes a `FunctionDeployment` containing JavaScript
which defines `handler(event)`, an optional `FunctionTrigger`, and an optional
five-field UTC cron. `list_functions` omits source; `get_function` includes it.
`invoke_function` serializes the supplied JSON body and deserializes the handler's
result into your chosen type. Use `invoke_function_response` to retain a custom
successful status, headers and raw text or empty body. The SDK does not replay an
uncertain invocation.

Use `list_function_logs` for invocation output and `list_function_deliveries`
for change-trigger attempts. A triggered function cannot write the collection it
watches. `delete_function` stops the function and its triggers. Server-side
validation, quotas and project permissions apply to every method.
