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

The tables browser (`/databases/{databaseId}/tables/live`) also listens to the
selected table's `terrascale.shape.v1` WebSocket hints. A hints-only reader uses
the current user's `sql:read` project token and the database's serving region.
It bootstraps with `offset=-1`, takes the handle and snapshot boundary from the
redirect, and opens `/tables/{table}/shape/ws` at that boundary. It downloads no
shape snapshots or chunks. `new_data` and `provisional` frames trigger the same
authorized SQL reads as the page; the HTML hash still decides whether to patch.
The .NET SDK's public `LiveShape` maintains a complete row snapshot, so this view
uses a small reader instead of materializing a second copy of the table.

The two-second server timer remains alongside hints because table creation and
deletion have no catalog shape subscription. It also covers tables without shape
support and unavailable or closed hint streams. The next SSE connection attempts
hints again. There is no browser polling or refresh button.

Only `tables-browser` is patched. The create-table dialog and its draft stay
outside it, and the selected table remains bound to the request query. The current
browser shows the first 100 rows and has no sort, filter or pagination controls;
every live read keeps that same query. Catalog changes, counts, columns and row
inserts, updates and deletes therefore use the same rendering as the initial page.
