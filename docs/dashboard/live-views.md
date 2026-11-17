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

Conflicts use `/databases/{databaseId}/conflicts/live` to patch only
`conflict-results`, leaving draft filter inputs alone. The stream carries the
selected table, date range and cursor, so changes appear within that result set.

Request logs use `/databases/{databaseId}/logs/live` to patch only
`request-log-results`, in newest-first order. Selecting a status filter removes
the live initializer and cancels that stream. The current request-log page has no
pagination or search. The database frame and log-drain controls stay outside the
live region.

Notifications use `/notifications/live` with `view=shell`, `view=inbox`, or
`view=dialog`. One shell stream updates both the sidebar bell and account-menu
unread count. The inbox and open dialog each have one stream carrying their own
status filter and page; closing the dialog removes its initializer. All reads
resolve the session and workspace again and read the persisted account inbox,
so marking read in another tab updates every visible region. Access loss patches
the affected region with `LiveErrorView` and closes its stream.

These three sources expose no consumable change signal to the dashboard. They
use the existing server-side `TimeProvider` timer and HTML hash comparison;
there is no browser polling or refresh button.
