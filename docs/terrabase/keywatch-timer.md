# In-flight key-watch timer lifecycle

Every keyed HTTP request and Postgres session retains its own watch. Normal completion disposes the pending
PeriodicTimer wait, which returns false; it does not cancel a Task.Delay and throw a cancellation exception.
An in-flight authority check is still cancelled when its request/session ends. Poll ticks coalesce while a check
runs, so a delayed check may be followed immediately by the next tick; the interval remains 250 milliseconds.

Admission still checks key status before serving. Positive cache answers still expire two seconds from the status
question's send time; refused/unavailable answers and expired authority still terminate in-flight work. The watch
retains the deadline at the previous answer's Until, and normal completion never cancels another request.
There is no shared watch, new cache, option, token retention, or change to token renewal on Postgres sessions.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
