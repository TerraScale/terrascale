# Hosted pages have a storage deadline

Every `/ui` request (sign-in, sign-up, reset, device approval, step-up) runs under the token endpoint's 10 s deadline
(`TokenEndpointDeadline`). When storage stalls past it, the request is cancelled and answers
`503 temporarily_unavailable` with `Retry-After: 2`, instead of holding the browser for ts-dotnet's 30 s request
timeout on each storage call. A write the deadline abandoned may still commit; every hosted mutation is a
conditional write, so a retry cannot apply it twice.
