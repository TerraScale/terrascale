# Customer JavaScript functions

This source candidate adds all seven function routes through database-scoped
handles. Root wrappers await the concurrent safety changes; live qualification
and publication remain separate work.

```python
from terrascale.functions import DatabaseFunctions, FunctionDeployment, FunctionTrigger

functions = DatabaseFunctions(client.database("main"))
deployed = functions.deploy_function("hello", FunctionDeployment(
    source="function handler(event) { return event.body; }",
    trigger=FunctionTrigger("orders", ["upsert"]),
))
listed = functions.list_functions()  # source is None in list results
loaded = functions.get_function("hello")
result = functions.invoke_function("hello", {"name": "Ada"})
logs = functions.list_function_logs("hello", limit=10)
deliveries = functions.list_function_deliveries("hello")
functions.delete_function("hello")
```

Use `AsyncDatabaseFunctions(async_client.database("main"))` and await each
method for the same typed DTOs. Invocation accepts JSON, including `None`, and
optional query values. Results can be any JSON value, a text response, or `None`
for an empty response. Source is limited to 256 KiB and invocation JSON to 1 MiB.
Cron expressions are five-field UTC; JavaScript and cron validation run on the
server. A trigger cannot write its watched collection. Trigger events default
to both upserts and deletes when `None`; delivery is at least once with retries.
Deploy/delete/invoke are never automatically replayed after a transport failure.
Reconcile an uncertain result before retrying. Logs are newest first, limited
to 1–100 per request. Pending deliveries expose attempts and error codes.
