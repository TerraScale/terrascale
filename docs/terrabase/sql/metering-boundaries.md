# SQL result and commit metering boundaries

Table reads stage their existing per-row operation units in the SQL result. Each
protocol accepts that charge once, when it accepts the result for its response:

- HTTP SQL accepts all read results after the atomic request commits successfully.
  A failed statement or refused commit discards the buffered read charges.
- PostgreSQL accepts a result after encoding its rows and command completion.
  Portal Describe may cache rows to infer their types, but Close, replacement,
  disconnect, or a refused Execute does not accept that pending read charge.

Already accepted interactive reads remain charged if the transaction later rolls
back. Transaction replay does not accept new read charges. Write units and
put/patch/delete operation telemetry, including collection write statistics, are
emitted once after the storage transaction commits. Replay reconstructs the tally;
failed statements and rolled-back transactions discard it.

These are protocol acceptance boundaries, not proof that a remote client received
every byte. Transport failure after acceptance does not refund a charge, and lost
acknowledgements do not refund committed writes. Partial PostgreSQL encoding and
streaming transport failures need separate delivery-policy coverage.
