# Live dashboard views

Replication, restore status and restore points open one Datastar SSE GET per
region at the view's `/live` route. `Common/Live/LiveView` renders the same Razor
slice as the page, hashes its HTML and sends a patch only when that hash changes.
The response ends on request cancellation or after fifteen minutes. Datastar
reconnects after a disconnect or the fifteen-minute close. A completed or failed
restore removes its live initializer in the final patch and closes its stream.

These sources currently have no change signal, so `PeriodicTimer` with the
injected `TimeProvider` checks them every two seconds on the server. The stream
accepts an `IAsyncEnumerable<DateTimeOffset>` of change hints so a source with a
change signal can replace the timer without changing the rendering loop.

Every read resolves the current session and account through `AccountRequest`,
refreshes database metadata and uses `DatabaseService.LookupAsync`, just as the
database pages do. Losing access or deleting the database replaces the live
region with the normal error message or not-found content and closes the stream.
There are no browser polling timers in these views.
