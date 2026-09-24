# Safe one-shot LZ4 evidence provenance addendum

Date: 2026-09-14

## Qualification status

The focused result recorded in [lz4-safe-one-shot-focused-tests-20260914.log](lz4-safe-one-shot-focused-tests-20260914.log) is historical evidence only. It reports 14 passing tests, but the captured command used `--no-build` and no build receipt was recorded that matches the assembly to the current source hashes. Therefore that result is not source-matched qualification for the current working-tree changes.

The existing focused-test log and its SHA-256 are preserved unchanged:

```text
93739025140dbb8657716a212aa97c7d986eb67dc224483771359e2ea0ab42ab  lz4-safe-one-shot-focused-tests-20260914.log
```

## Current source state

The current working-tree source hashes are:

```text
6f4779726aabe85d90782098fe86dd7b4af327b8ffb25364f93d13b7f2f254f4  src/TerraBase.ZoneTree/Compression/LZ4DataCompression.cs
cbc54eb83aa206e5b88166051b700f3f14861a98a957e93a7999cd4217e081bc  tests/TerraBase.ZoneTree.Tests/CompressionIntegrityTests.cs
```

These files are modified relative to TerraBase revision `fa68755e2c64b5137ebca2945d33c8f850a0944a`.

## Existing binary artifact state

The following files were present when this addendum was written. Their hashes document artifact state only; they do not establish that the binaries were built from the source hashes above:

```text
733d7f534d8661a562e4f82135efbd5e92d19ae699a32f3ba1b92a7b58c6245d  tests/TerraBase.ZoneTree.Tests/bin/Debug/net10.0/TerraBase.ZoneTree.Tests.dll
4563ec000747dcb7b5083bc86054dc601d9f652f1edadba6c2c8b1469043fa0a  tests/TerraBase.ZoneTree.Tests/bin/Debug/net10.0/TerraBase.ZoneTree.dll
3091dae46e0ed6e449222dd762aaa8c03205d86d58ccead737dbded3f1f890c9  src/TerraBase.ZoneTree/bin/Debug/net10.0/TerraBase.ZoneTree.dll
```

Observed timestamps were:

```text
2026-09-14 22:54:40.568566375 -0300  tests/TerraBase.ZoneTree.Tests/bin/Debug/net10.0/TerraBase.ZoneTree.Tests.dll
2026-09-14 22:54:39.402540382 -0300  tests/TerraBase.ZoneTree.Tests/bin/Debug/net10.0/TerraBase.ZoneTree.dll
2026-09-14 22:55:58.011292985 -0300  src/TerraBase.ZoneTree/bin/Debug/net10.0/TerraBase.ZoneTree.dll
```

A subsequent low-priority source-matched build must replace the pending entries with the exact build command, exit code, source hashes before and after the build, build log hash, and resulting assembly hashes. The focused selector can then be run against that built assembly and recorded as qualified evidence.
