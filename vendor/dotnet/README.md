# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet `0.0.12-q08unicode.ge7b1fffb45f3` from
merged ts-dotnet revision `e7b1fffb45f37563adc26fb1739213bc582d896e` (PR82).
Document and schema requests use compact Unicode JSON, preserving admitted
values within the producer’s UTF-8 request limits.

This unpublished, revision-qualified identity avoids replacing bytes under the
existing 0.0.12 package identity. Only the pack-time version differs from that
source revision. Two clean locked builds produced identical bytes; installed
HTTP consumers passed on net10.0 and net11.0. NuGet publication is not claimed.

`provenance.json` records the immutable source revision/tree, toolchain, package,
assembly and contract hashes. CI verifies these bytes and the central pin.
Earlier artifacts remain retained; only the revision-qualified package is selected.
