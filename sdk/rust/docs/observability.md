`Client::list_regions()` retrieves the current ordered serving IDs anonymously from
`/v1/regions`; these are placement IDs, not a hard-coded SDK region enum.
`Client::get_project_metrics()` returns the exact Prometheus 0.0.4 text for the
project and databases authorized by the token (`databases:read`). Observations are
best effort, not billing: rates cover five complete UTC minutes, latency the
newest complete observed minute, and storage the newest sample within an hour.
Both calls obey the existing response-size bound and reject non-200 responses.
