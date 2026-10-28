# In-flight key-watch timer lifecycle

Every keyed HTTP request and Postgres session retains its own watch. Normal completion disposes the pending
PeriodicTimer wait, which returns false; it does not cancel a Task.Delay and throw a cancellation exception.
An in-flight authority check is still cancelled when its request/session ends. Poll ticks coalesce while a check
runs, so a delayed check may be followed immediately by the next tick; the interval remains 250 milliseconds.

Admission still checks key status before serving. Positive cache answers still expire two seconds from the status
question's send time; refused/unavailable answers and expired authority still terminate in-flight work. The watch
retains the deadline at the previous answer's Until, and normal completion never cancels another request.
There is no shared watch, new cache, option, token retention, or change to token renewal on Postgres sessions.

The Fast integration case opens an authenticated HTTP changes feed and verifies it still receives a change after
one successful poll. It then withdraws key authority or makes status unavailable, advances the test clock to the
existing 2.25-second bound, and requires the established feed to end.
Existing admission and Postgres revocation cases remain active. Benchmark first-chance exception counts are
process-wide mechanism evidence, not a substitute for revocation integration coverage or latency measurements.
