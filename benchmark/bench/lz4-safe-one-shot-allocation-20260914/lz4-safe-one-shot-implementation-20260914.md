# Safe one-shot LZ4 adapter correction

Date: 2026-09-14

## Change

`LZ4DataCompression.Compress` now calls NativeCompressions' `LZ4.Compress(ReadOnlySpan<byte>, in LZ4CompressionOptions)` overload and returns its exact output as `Memory<byte>`. That package overload constructs a real `LZ4F_preferences_t`, asks native LZ4 for the matching frame bound, rents its temporary buffer, and copies only the written output. The adapter continues to set the actual input length, `Max4MB`, content checksum, and block checksum in the original options.

The adapter now accepts a declared LZ4 content size of zero only when the caller expects zero bytes. Decoder completion still requires `OperationStatus.Done`, complete compressed input consumption, and exactly the expected output length. Required checksums and positive length matching remain enforced.

## Focused validation

Source state tested: TerraBase revision `fa68755e2c64b5137ebca2945d33c8f850a0944a` with the two working-tree changes listed below.

```text
6f4779726aabe85d90782098fe86dd7b4af327b8ffb25364f93d13b7f2f254f4  src/TerraBase.ZoneTree/Compression/LZ4DataCompression.cs
cbc54eb83aa206e5b88166051b700f3f14861a98a957e93a7999cd4217e081bc  tests/TerraBase.ZoneTree.Tests/CompressionIntegrityTests.cs
```

Targeted command:

```text
nice -n 19 dotnet run \
  --project tests/TerraBase.ZoneTree.Tests/TerraBase.ZoneTree.Tests.csproj \
  --configuration Debug --no-build -- \
  --treenode-filter '/*/*/CompressionIntegrityTests/*' \
  --no-ansi --progress off --show-test-results all
```

Result: `total: 14`, `failed: 0`, `succeeded: 14`, `skipped: 0`. The complete output is [lz4-safe-one-shot-focused-tests-20260914.log](lz4-safe-one-shot-focused-tests-20260914.log), SHA-256 `93739025140dbb8657716a212aa97c7d986eb67dc224483771359e2ea0ab42ab`.

The tests cover:

- empty, 4 KiB, 64 KiB, 256 KiB, and 4 MiB input sizes;
- compressible and deterministic incompressible data;
- content and block checksum preservation;
- empty-frame round trip with both checksums;
- corrupted nonempty and empty checksums;
- trailing compressed data;
- empty/positive declared-length mismatches;
- missing checksums and excessive declared length.

`git diff --check` passed for both owned source files. The focused build/test run completed without warnings or errors.

## Safe one-shot allocation diagnostic

This is a managed allocation diagnostic only; it makes no throughput claim. The harness uses a 4 MiB deterministic incompressible input and measures `GC.GetAllocatedBytesForCurrentThread` around one `LZ4DataCompression.Compress` call.

```text
mode=cold input=4194304 output=4194335 allocated-current-thread=12593784 elapsed-ms=220.366
mode=warm input=4194304 output=4194335 allocated-current-thread=4194400 elapsed-ms=182.421
```

`cold` is a fresh process and includes first-call runtime/JIT effects. `warm` performs three prewarm compressions and a collection before measuring; its allocation is approximately the exact output array plus small managed overhead. The source and project are preserved at [lz4-safe-one-shot-allocation-harness-20260914.cs](lz4-safe-one-shot-allocation-harness-20260914.cs) and [lz4-safe-one-shot-allocation-harness-20260914.csproj](lz4-safe-one-shot-allocation-harness-20260914.csproj). Outputs are [cold](lz4-safe-one-shot-allocation-cold-20260914.log) and [warm](lz4-safe-one-shot-allocation-warm-20260914.log).

Harness hashes:

```text
cf946f2b27c8c290c009b72ccc8d627df031181120e3c5d9a75fcf3e3b92e6d7  lz4-safe-one-shot-allocation-harness-20260914.cs
264f6f5e3385ea3da1670451d1d5edf46681c5f56b91f7c398db19b9b2ff9748  lz4-safe-one-shot-allocation-cold-20260914.log
b39cb95517071221d11146fd32d533bafaa470bb7b6ab4fd9cffd778d87823f8  lz4-safe-one-shot-allocation-warm-20260914.log
```

No benchmark/default/other-codec files were changed.
