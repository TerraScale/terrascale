# TerraBase test resources

The protocol and API test projects carry the exact golden-vector bytes they
exercise under their own `vectors/` directories. Their project files copy
those tracked resources beside the test binaries; they do not rely on a
separate Rust checkout being present while the .NET projects are built.

The TBEP files in
`tests/TerraBase.Protocol.Tests/vectors/tbep/` are the engine corpus from
TerraBaseV2 commit `59ce520ab7840db022fe3c0ef3a5734bb5fce27b`. The copy
manifest records each file's byte count and SHA-256 digest, and
`TbepGoldenVectorsTests.VendoredCorpusMatchesItsCopyManifest` checks the
record before replay.

The sync files in
`tests/TerraBase.Api.Tests/vectors/terrascale.sync.v2/` are the
`terrabase-sync-core` corpus from commit
`d67c4c43f8800675f8b093448e1435afd13ab2b9`. Its manifest is checked by
`SyncGoldenVectorTests.VendoredCorpusMatchesItsCopyManifest` before the
categories are consumed.

These resources prove the C# codec and host boundary against recorded
contract data. The end-to-end API rows and real-engine rows still need the
external `crates/terrabase-tbep` source and Cargo toolchain. When that source
is absent, `EngineHarness` classifies those rows as unavailable and skips
them with an explicit reason. Once the source is present, a failed Cargo
build remains a test failure so engine regressions are not hidden as skips.
