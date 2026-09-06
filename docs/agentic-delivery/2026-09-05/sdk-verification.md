# SDK integration verification

This record covers the shared SDK work on 2026-09-05. It is not launch approval.
Concurrent workspace synchronization commits changed repository revisions during
verification. The final producer bundle and final package digests must be
recorded after their checks finish; earlier passing checks do not qualify later
source changes.

## Cleanup review

TypeScript package exports, build inputs, route captures, and dashboard callers
were traced. No tracked dist/cache artifacts were found. The local SQLite
entrypoint remains an active dashboard development dependency; retirement waits
for qualified public auth and product persistence. Its local tests do not
qualify production. The local schema generator retained useful generation but
lost unused aliases/options and injected retired document metadata. Its tests
now run in the standard Vitest gate.

Go and .NET tracked Python bytecode was removed with cache ignore rules. Go's
unused duplicate session storage was removed. .NET removed unreferenced retired
database topology/create DTOs and an ignored hard-delete option. Rust retained
its customer SDK and removed duplicated placement observation logic. Human
instruction files and the shared goal were preserved.

## Changes checked during integration

- Region repair respects strict affinity. TypeScript repairs conflicting
  per-request hints using the authoritative 421 placement.
- Ordinary HTTP redirects are disabled to prevent credential forwarding and
  mutation replay. Supplied .NET HttpClient handlers must disable redirects;
  its supported DI handler does so.
- TypeScript and .NET return long Retry-After responses instead of retrying
  before the server deadline.
- Document/SQL integers retain precision. Go uses json.Number; TypeScript read
  types distinguish bigint-capable results from JSON write inputs. Signed and
  unsigned management counters have separate range checks.
- Independent producer HTTP and reactive fixtures exercise real decoders and
  state transitions. They exposed and corrected .NET insert field order,
  text/binary marker confusion, and oversized position handling.
- Public management clients implement the producer's source-backed schema,
  usage, indexes, webhook, export, development, and provisioning operations.
  Creation distinguishes immediate creation from accepted provisioning.
- Export downloads preserve binary bytes. Cancellation, nullable progress,
  and pagination retain their public semantics.
- Better Auth exports support Node and Workerd, reject browser-only imports,
  and select Workerd before its shared browser condition. An actual dashboard
  Workerd rerun cleared the earlier module-resolution failure; a separate
  React runtime issue remained under dashboard investigation.
- npm qualification accepts an exact prepacked tarball. ts-deploy invokes the
  strict qualifier before signing/publication and refuses changed bytes. Static
  and dynamic browser imports of the server adapter are rejected.

## Evidence and remaining gates

Intermediate checks passed for Go, Rust, TypeScript and .NET against the earlier
27-operation producer bundle. Rust also passed a clean HTTP archive consumer;
Go an isolated module ZIP consumer; .NET a standalone NuGet consumer. TypeScript
qualified an exact tarball under Node 22.23.2 and 24.20.0, including combined
Workerd/browser package resolution. These checks preceded later API additions
and cannot qualify the final bundle or final packages.

Final producer generation is waiting for the consolidated engine compile. All
four SDKs require a fresh full check and package check after that bundle lands.
The source route count is changing; conformance must enumerate the actual final
manifest and every published success status, not a fixed count.

Immutable public contract publication, actual provider/API success and denial,
real Better Auth persistence/concurrency/revocation, and end-to-end launch
qualification remain unpassed. Missing live credentials are skips, not evidence.
TypeScript's explicit `contract_gap` methods and remaining uncaptured producer
operations must stay visible until implemented and qualified.
