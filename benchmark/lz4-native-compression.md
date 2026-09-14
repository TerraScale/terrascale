# NativeCompressions LZ4 block sizing

This comparison measures the `DataCompression` primitive used by ZoneTree.
The candidate keeps the existing LZ4 frame, content-size, and checksum
contract, and requests a 4 MiB maximum frame block so a bounded 4 MiB ZoneTree
block is not split into 64 KiB frame blocks.

Both runs used the same Release benchmark command, payload seed, five warmup
samples, five measured samples, 20 operations per sample, and one worker:

```text
nice -n 19 dotnet TerraBase.Benchmarks.dll --workload compression \
  --samples 5 --warmup 5 --operations 20 --concurrency 1
```

| Profile | Payload | Baseline compressed bytes | Candidate compressed bytes | Change | Baseline warm median p50 (µs) | Candidate warm median p50 (µs) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| small document | 4 KiB | 173 | 173 | 0 B | 43.48 | 40.54 |
| large document | 256 KiB | 1,236 | 1,185 | -51 B (-4.1%) | 1,325.58 | 315.89 |
| incompressible block | 4 MiB | 4,194,839 | 4,194,335 | -504 B (-0.012%) | 214,393.12 | 199,752.52 |

The size reduction is the direct storage effect of using one 4 MiB frame block
for these bounded inputs. The latency values are retained as directional
primitive measurements from one run per source state; they are not an
end-to-end ZoneTree or TerraBase storage claim. The baseline receipt is
`/tmp/terrabase-compression-baseline/compression-summary.md`; the candidate
receipt is `/tmp/terrabase-compression-max4mb-final/compression-summary.md`.
