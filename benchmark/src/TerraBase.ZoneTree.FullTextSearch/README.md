# TerraBase.ZoneTree.FullTextSearch

This project contains the provider-independent full-text search core used by
TerraBase. It follows the useful parts of the ZoneTree full-text search model:
word tokenization, an inverted token-to-record index, phrase matching, facets,
and bounded result pagination.

The current index is process-local. A storage provider can own persistence and
rebuild the index by replaying `AddRecord` and `AddFacet` calls.
